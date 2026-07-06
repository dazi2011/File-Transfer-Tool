using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web;
using System.Web.UI;

public partial class upload_chunk : Page
{
    private const int CopyBufferBytes = 1024 * 1024;
    private const int MaxChunkCount = 1000000;
    private static readonly object MetadataLock = new object();

    protected void Page_Load(object sender, EventArgs e)
    {
        Server.ScriptTimeout = 43200;
        Response.ContentType = "application/json";
        Response.TrySkipIisCustomErrors = true;
        TransferUtility.AddSecurityHeaders(Response);

        if (!String.Equals(Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            WriteError(405, "Only POST is allowed.");
            return;
        }

        if (!TransferSecurity.RequireAuthorized(Request, Response))
        {
            return;
        }

        try
        {
            TransferUtility.CleanupExpiredTempUploadsIfDue();

            UploadChunkResult result = SaveChunk();
            WriteJson(200, ResultToJson(result));
        }
        catch (InvalidOperationException ex)
        {
            WriteError(400, ex.Message);
        }
        catch (HttpException ex)
        {
            WriteError(400, ex.Message);
        }
        catch (Exception ex)
        {
            WriteError(500, "Upload failed: " + ex.Message);
        }
    }

    private UploadChunkResult SaveChunk()
    {
        string uploadId = TransferUtility.SanitizeUploadId(Request.Form["uploadId"]);
        string group = TransferUtility.NormalizeGroup(Request.Form["group"]);
        string fileName = TransferUtility.SanitizeFileName(Request.Form["fileName"]);
        int chunkIndex = TransferUtility.ParseIntForm(Request, "chunkIndex");
        int totalChunks = TransferUtility.ParseIntForm(Request, "totalChunks");
        long totalSize = TransferUtility.ParseLongForm(Request, "totalSize");
        long chunkStart = TransferUtility.ParseLongForm(Request, "chunkStart");
        long chunkSize = TransferUtility.ParseLongForm(Request, "chunkSize");

        if (totalChunks <= 0 || totalChunks > MaxChunkCount)
        {
            throw new InvalidOperationException("Chunk count is outside the supported range.");
        }
        if (chunkIndex < 0 || chunkIndex >= totalChunks)
        {
            throw new InvalidOperationException("Chunk index is outside the supported range.");
        }
        if (totalSize < 0)
        {
            throw new InvalidOperationException("Total file size cannot be negative.");
        }
        if (chunkSize <= 0 || chunkSize > TransferUtility.GetMaxChunkBytes())
        {
            throw new InvalidOperationException("Chunk size is outside the supported range.");
        }
        if (chunkStart < 0 || chunkStart > totalSize)
        {
            throw new InvalidOperationException("Chunk start is outside the file range.");
        }

        long expectedTotalChunks = totalSize == 0 ? 1 : (((totalSize - 1) / chunkSize) + 1);
        if (expectedTotalChunks != totalChunks)
        {
            throw new InvalidOperationException("Chunk count does not match the declared chunk size.");
        }

        long maxFileBytes = TransferUtility.GetMaxFileBytes();
        if (maxFileBytes > 0 && totalSize > maxFileBytes)
        {
            throw new InvalidOperationException("File is larger than the configured TransferMaxFileBytes limit.");
        }

        HttpPostedFile chunk = Request.Files["chunk"];
        if (chunk == null && Request.Files.Count > 0)
        {
            chunk = Request.Files[0];
        }
        if (chunk == null)
        {
            throw new InvalidOperationException("Chunk file is required.");
        }
        if (chunk.ContentLength > TransferUtility.GetMaxChunkBytes())
        {
            throw new InvalidOperationException("Chunk is larger than the configured TransferMaxChunkBytes limit.");
        }
        if (totalSize > 0 && chunk.ContentLength == 0)
        {
            throw new InvalidOperationException("Chunk is empty.");
        }

        long expectedChunkStart = chunkIndex * chunkSize;
        long expectedChunkLength = Math.Min(chunkSize, totalSize - expectedChunkStart);
        if (chunkStart != expectedChunkStart)
        {
            throw new InvalidOperationException("Chunk start does not match the chunk index.");
        }
        if (chunk.ContentLength != expectedChunkLength)
        {
            throw new InvalidOperationException("Chunk length does not match the declared upload layout.");
        }

        string sessionPath = TransferUtility.GetTempUploadPath(uploadId);
        TransferUtility.TouchTempUploadSession(sessionPath);
        WriteOrValidateMetadata(sessionPath, fileName, group, totalChunks, totalSize, chunkSize);

        long bytesWritten = WriteChunkToStaging(sessionPath, chunk, chunkIndex, chunkStart, totalSize);

        TransferUtility.TouchTempUploadSession(sessionPath);

        UploadChunkResult result = new UploadChunkResult();
        result.UploadId = uploadId;
        result.ChunkIndex = chunkIndex;
        result.TotalChunks = totalChunks;
        result.BytesReceived = bytesWritten;
        result.Complete = false;

        if (!AllChunksPresent(sessionPath, totalChunks))
        {
            return result;
        }

        FileStream mergeLock = null;
        if (!TryAcquireMergeLock(sessionPath, out mergeLock))
        {
            result.Merging = true;
            return result;
        }

        using (mergeLock)
        {
            TransferUtility.TouchTempUploadSession(sessionPath);
            MergeResult mergeResult = FinalizeStagedUpload(sessionPath, group, fileName, totalSize);
            result.Complete = true;
            result.Merging = false;
            result.StoredFileName = Path.GetFileName(mergeResult.FilePath);
            result.StoredSize = mergeResult.Size;
            result.Sha256 = mergeResult.Sha256;
        }

        try
        {
            Directory.Delete(sessionPath, true);
        }
        catch
        {
            // A completed upload should not fail because temporary cleanup had a transient lock.
        }

        return result;
    }

    private static long WriteChunkToStaging(string sessionPath, HttpPostedFile postedFile, int chunkIndex, long chunkStart, long totalSize)
    {
        string markerPath = Path.Combine(sessionPath, GetChunkMarkerName(chunkIndex));
        string stagingPath = GetStagingPath(sessionPath);

        using (FileStream sessionLock = AcquireSessionLock(sessionPath))
        {
            if (File.Exists(markerPath))
            {
                return postedFile.ContentLength;
            }

            using (FileStream output = new FileStream(stagingPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, CopyBufferBytes, FileOptions.RandomAccess))
            {
                if (output.Length != totalSize)
                {
                    output.SetLength(totalSize);
                }

                output.Seek(chunkStart, SeekOrigin.Begin);
                long bytesWritten = CopyStream(postedFile.InputStream, output);
                if (bytesWritten != postedFile.ContentLength)
                {
                    throw new IOException("Chunk length changed while saving.");
                }

                output.Flush(true);
                File.WriteAllText(markerPath, bytesWritten.ToString(CultureInfo.InvariantCulture), Encoding.UTF8);
                sessionLock.SetLength(0);
                return bytesWritten;
            }
        }
    }

    private static MergeResult FinalizeStagedUpload(string sessionPath, string group, string fileName, long expectedSize)
    {
        string finalPath = TransferUtility.GetUniqueDestinationPath(group, fileName);
        string stagingPath = GetStagingPath(sessionPath);
        FileInfo stagingFile = new FileInfo(stagingPath);
        if (!stagingFile.Exists)
        {
            throw new IOException("Staged upload file is missing.");
        }
        if (stagingFile.Length != expectedSize)
        {
            throw new IOException("Staged upload file size does not match upload metadata.");
        }

        string sha256 = TransferUtility.ShouldComputeSha256() ? TransferUtility.Sha256Hex(stagingPath) : "";
        File.Move(stagingPath, finalPath);

        MergeResult result = new MergeResult();
        result.FilePath = finalPath;
        result.Size = expectedSize;
        result.Sha256 = sha256;
        return result;
    }

    private static long CopyStream(Stream input, Stream output)
    {
        byte[] buffer = new byte[CopyBufferBytes];
        long total = 0;
        int read;

        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            total += read;
        }

        return total;
    }

    private static void WriteOrValidateMetadata(string sessionPath, string fileName, string group, int totalChunks, long totalSize, long chunkSize)
    {
        string metadataPath = Path.Combine(sessionPath, "upload.meta");
        string metadata = "group=" + group + "\n" +
                          "fileName=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(fileName)) + "\n" +
                          "totalChunks=" + totalChunks.ToString(CultureInfo.InvariantCulture) + "\n" +
                          "totalSize=" + totalSize.ToString(CultureInfo.InvariantCulture) + "\n" +
                          "chunkSize=" + chunkSize.ToString(CultureInfo.InvariantCulture) + "\n";

        lock (MetadataLock)
        {
            if (!File.Exists(metadataPath))
            {
                File.WriteAllText(metadataPath, metadata, Encoding.UTF8);
                return;
            }

            if (!String.Equals(File.ReadAllText(metadataPath, Encoding.UTF8), metadata, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Upload metadata changed between chunks.");
            }
        }
    }

    private static bool AllChunksPresent(string sessionPath, int totalChunks)
    {
        for (int i = 0; i < totalChunks; i++)
        {
            if (!File.Exists(Path.Combine(sessionPath, GetChunkMarkerName(i))))
            {
                return false;
            }
        }

        return true;
    }

    private static FileStream AcquireSessionLock(string sessionPath)
    {
        string lockPath = Path.Combine(sessionPath, "session.lock");
        for (int i = 0; i < 7200; i++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(250);
            }
        }

        throw new IOException("Timed out waiting for the upload session lock.");
    }

    private static bool TryAcquireMergeLock(string sessionPath, out FileStream mergeLock)
    {
        string lockPath = Path.Combine(sessionPath, "merge.lock");
        try
        {
            mergeLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            mergeLock.SetLength(0);
            return true;
        }
        catch (IOException)
        {
            mergeLock = null;
            return false;
        }
    }

    private static string GetChunkMarkerName(int chunkIndex)
    {
        return "chunk_" + chunkIndex.ToString("D8", CultureInfo.InvariantCulture) + ".ok";
    }

    private static string GetStagingPath(string sessionPath)
    {
        return Path.Combine(sessionPath, "upload.staging");
    }

    private void WriteError(int statusCode, string message)
    {
        Response.StatusCode = statusCode;
        WriteJson(statusCode, "{\"ok\":false,\"error\":\"" + JsonEscape(message) + "\"}");
    }

    private void WriteJson(int statusCode, string json)
    {
        Response.StatusCode = statusCode;
        Response.Write(json);
        Context.ApplicationInstance.CompleteRequest();
    }

    private static string ResultToJson(UploadChunkResult result)
    {
        StringBuilder builder = new StringBuilder();
        builder.Append("{\"ok\":true");
        builder.Append(",\"uploadId\":\"").Append(JsonEscape(result.UploadId)).Append("\"");
        builder.Append(",\"chunkIndex\":").Append(result.ChunkIndex.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"totalChunks\":").Append(result.TotalChunks.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"bytesReceived\":").Append(result.BytesReceived.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"complete\":").Append(result.Complete ? "true" : "false");
        builder.Append(",\"merging\":").Append(result.Merging ? "true" : "false");

        if (!String.IsNullOrEmpty(result.StoredFileName))
        {
            builder.Append(",\"fileName\":\"").Append(JsonEscape(result.StoredFileName)).Append("\"");
            builder.Append(",\"size\":").Append(result.StoredSize.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"sizeText\":\"").Append(JsonEscape(TransferUtility.FormatFileSize(result.StoredSize))).Append("\"");
            if (!String.IsNullOrEmpty(result.Sha256))
            {
                builder.Append(",\"sha256\":\"").Append(JsonEscape(result.Sha256)).Append("\"");
            }
        }

        builder.Append("}");
        return builder.ToString();
    }

    private static string JsonEscape(string value)
    {
        return TransferUtility.JavaScript(value ?? String.Empty);
    }

    private class UploadChunkResult
    {
        public string UploadId;
        public int ChunkIndex;
        public int TotalChunks;
        public long BytesReceived;
        public bool Complete;
        public bool Merging;
        public string StoredFileName;
        public long StoredSize;
        public string Sha256;
    }

    private class MergeResult
    {
        public string FilePath;
        public long Size;
        public string Sha256;
    }
}
