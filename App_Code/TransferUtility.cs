using System;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web;

public static class TransferUtility
{
    public const string GroupDeletable = "group1";
    public const string GroupReadOnly = "group2";
    public const string GroupHidden = "group3";

    private const int DefaultMaxChunkBytes = 16 * 1024 * 1024;
    private const int MinChunkBytes = 1024 * 1024;
    private const int MaxChunkBytes = 256 * 1024 * 1024;
    private static readonly object CleanupLock = new object();
    private static DateTime LastCleanupUtc = DateTime.MinValue;

    public static string NormalizeGroup(string group)
    {
        if (String.IsNullOrWhiteSpace(group))
        {
            return GroupDeletable;
        }

        group = group.Trim().ToLowerInvariant();
        if (group == GroupDeletable || group == GroupReadOnly || group == GroupHidden)
        {
            return group;
        }

        throw new InvalidOperationException("Invalid upload group.");
    }

    public static string SanitizeFileName(string rawFileName)
    {
        string fileName = Path.GetFileName(rawFileName ?? String.Empty);
        if (String.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("File name is required.");
        }

        fileName = fileName.Trim();
        char[] invalidChars = Path.GetInvalidFileNameChars();
        StringBuilder builder = new StringBuilder(fileName.Length);

        for (int i = 0; i < fileName.Length; i++)
        {
            char current = fileName[i];
            bool invalid = Char.IsControl(current);
            for (int j = 0; j < invalidChars.Length; j++)
            {
                if (current == invalidChars[j])
                {
                    invalid = true;
                    break;
                }
            }

            builder.Append(invalid ? '_' : current);
        }

        fileName = builder.ToString().Trim('.', ' ');
        if (String.IsNullOrWhiteSpace(fileName) || fileName == "." || fileName == "..")
        {
            throw new InvalidOperationException("File name is not valid.");
        }

        if (fileName.Length <= 180)
        {
            return fileName;
        }

        string extension = Path.GetExtension(fileName);
        string name = Path.GetFileNameWithoutExtension(fileName);
        int maxNameLength = Math.Max(1, 180 - extension.Length);
        return name.Substring(0, Math.Min(name.Length, maxNameLength)) + extension;
    }

    public static string SanitizeUploadId(string uploadId)
    {
        if (String.IsNullOrWhiteSpace(uploadId) || uploadId.Length < 8 || uploadId.Length > 96)
        {
            throw new InvalidOperationException("Upload id is not valid.");
        }

        for (int i = 0; i < uploadId.Length; i++)
        {
            char c = uploadId[i];
            bool valid = (c >= 'a' && c <= 'z') ||
                         (c >= 'A' && c <= 'Z') ||
                         (c >= '0' && c <= '9') ||
                         c == '-' ||
                         c == '_';
            if (!valid)
            {
                throw new InvalidOperationException("Upload id is not valid.");
            }
        }

        return uploadId;
    }

    public static string GetGroupStoragePath(string group)
    {
        group = NormalizeGroup(group);
        string root = MapConfiguredPath(GetSetting("TransferStorageRoot", "~/App_Data/TransferFiles"));
        string path = Path.Combine(root, group);
        EnsureDirectory(path);
        return path;
    }

    public static string GetTempUploadRoot()
    {
        string path = MapConfiguredPath(GetSetting("TransferTempRoot", "~/App_Data/TransferTemp"));
        EnsureDirectory(path);
        return path;
    }

    public static string GetTempUploadPath(string uploadId)
    {
        uploadId = SanitizeUploadId(uploadId);
        string path = Path.Combine(GetTempUploadRoot(), uploadId);
        EnsureDirectory(path);
        return path;
    }

    public static string GetExistingFilePath(string group, string fileName)
    {
        string groupPath = GetGroupStoragePath(group);
        string safeFileName = SanitizeFileName(fileName);
        string filePath = Path.GetFullPath(Path.Combine(groupPath, safeFileName));
        EnsurePathWithin(filePath, groupPath);
        return filePath;
    }

    public static string GetUniqueDestinationPath(string group, string fileName)
    {
        string groupPath = GetGroupStoragePath(group);
        string safeFileName = SanitizeFileName(fileName);
        string candidate = Path.GetFullPath(Path.Combine(groupPath, safeFileName));
        EnsurePathWithin(candidate, groupPath);

        if (!File.Exists(candidate))
        {
            return candidate;
        }

        string name = Path.GetFileNameWithoutExtension(safeFileName);
        string extension = Path.GetExtension(safeFileName);
        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        for (int i = 1; i < 10000; i++)
        {
            string nextName = name + "_" + stamp + "_" + i.ToString(CultureInfo.InvariantCulture) + extension;
            candidate = Path.GetFullPath(Path.Combine(groupPath, nextName));
            EnsurePathWithin(candidate, groupPath);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Unable to allocate a unique destination file name.");
    }

    public static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    public static void EnsurePathWithin(string childPath, string parentPath)
    {
        string fullChild = Path.GetFullPath(childPath);
        string fullParent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        fullParent += Path.DirectorySeparatorChar;

        if (!fullChild.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved path escapes the storage root.");
        }
    }

    public static int GetMaxChunkBytes()
    {
        int configured;
        if (!Int32.TryParse(GetSetting("TransferMaxChunkBytes", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out configured))
        {
            configured = DefaultMaxChunkBytes;
        }

        return Math.Max(MinChunkBytes, Math.Min(MaxChunkBytes, configured));
    }

    public static long GetMaxFileBytes()
    {
        long configured;
        if (!Int64.TryParse(GetSetting("TransferMaxFileBytes", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out configured))
        {
            return 0;
        }

        return Math.Max(0, configured);
    }

    public static int GetClientParallelUploads()
    {
        int configured;
        if (!Int32.TryParse(GetSetting("TransferParallelUploads", "4"), NumberStyles.Integer, CultureInfo.InvariantCulture, out configured))
        {
            configured = 4;
        }

        return Math.Max(1, Math.Min(8, configured));
    }

    public static bool ShouldComputeSha256()
    {
        string configured = GetSetting("TransferComputeSha256", "false");
        return String.Equals(configured, "true", StringComparison.OrdinalIgnoreCase) ||
               String.Equals(configured, "1", StringComparison.OrdinalIgnoreCase) ||
               String.Equals(configured, "yes", StringComparison.OrdinalIgnoreCase);
    }

    public static TimeSpan GetTempUploadMaxAge()
    {
        int configured;
        if (!Int32.TryParse(GetSetting("TransferTempMaxAgeMinutes", "60"), NumberStyles.Integer, CultureInfo.InvariantCulture, out configured))
        {
            configured = 60;
        }

        return TimeSpan.FromMinutes(Math.Max(5, configured));
    }

    public static TimeSpan GetTempCleanupInterval()
    {
        int configured;
        if (!Int32.TryParse(GetSetting("TransferTempCleanupIntervalMinutes", "15"), NumberStyles.Integer, CultureInfo.InvariantCulture, out configured))
        {
            configured = 15;
        }

        return TimeSpan.FromMinutes(Math.Max(1, configured));
    }

    public static string FormatFileSize(long bytes)
    {
        string[] units = new string[] { "B", "KB", "MB", "GB", "TB", "PB" };
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size = size / 1024;
            unit++;
        }

        if (unit == 0)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " " + units[unit];
        }

        return size.ToString(size >= 100 ? "0.#" : "0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string Html(string value)
    {
        return HttpUtility.HtmlEncode(value ?? String.Empty);
    }

    public static string Url(string value)
    {
        return HttpUtility.UrlEncode(value ?? String.Empty);
    }

    public static string JavaScript(string value)
    {
        return HttpUtility.JavaScriptStringEncode(value ?? String.Empty);
    }

    public static string Sha256Hex(string filePath)
    {
        using (SHA256 sha256 = SHA256.Create())
        using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
        {
            return ToHex(sha256.ComputeHash(stream));
        }
    }

    public static string ToHex(byte[] bytes)
    {
        StringBuilder builder = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++)
        {
            builder.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    public static void AddSecurityHeaders(HttpResponse response)
    {
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Cache-Control"] = "no-store";
    }

    public static void CleanupExpiredTempUploads(TimeSpan maxAge)
    {
        string root = GetTempUploadRoot();
        DirectoryInfo rootInfo = new DirectoryInfo(root);
        DateTime cutoff = DateTime.UtcNow.Subtract(maxAge);

        foreach (DirectoryInfo directory in rootInfo.GetDirectories())
        {
            try
            {
                if (IsExpiredTempDirectory(directory, cutoff) && !HasActiveTempLocks(directory))
                {
                    directory.Delete(true);
                }
            }
            catch
            {
                // Best-effort cleanup; active uploads must never fail because cleanup could not remove old data.
            }
        }
    }

    public static void CleanupExpiredTempUploadsIfDue()
    {
        TimeSpan interval = GetTempCleanupInterval();

        lock (CleanupLock)
        {
            if (DateTime.UtcNow.Subtract(LastCleanupUtc) < interval)
            {
                return;
            }

            LastCleanupUtc = DateTime.UtcNow;
        }

        CleanupExpiredTempUploads(GetTempUploadMaxAge());
    }

    public static void TouchTempUploadSession(string sessionPath)
    {
        EnsureDirectory(sessionPath);
        string touchPath = Path.Combine(sessionPath, ".lastactivity");
        File.WriteAllText(touchPath, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), Encoding.UTF8);
        Directory.SetLastWriteTimeUtc(sessionPath, DateTime.UtcNow);
    }

    public static long ParseLongForm(HttpRequest request, string key)
    {
        long value;
        if (!Int64.TryParse(request.Form[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            throw new InvalidOperationException("Invalid numeric value for " + key + ".");
        }

        return value;
    }

    public static int ParseIntForm(HttpRequest request, string key)
    {
        int value;
        if (!Int32.TryParse(request.Form[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            throw new InvalidOperationException("Invalid numeric value for " + key + ".");
        }

        return value;
    }

    public static string GetSetting(string key, string fallback)
    {
        string value = ConfigurationManager.AppSettings[key];
        return String.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string MapConfiguredPath(string configuredPath)
    {
        if (String.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = "~/App_Data/TransferFiles";
        }

        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        if (!configuredPath.StartsWith("~", StringComparison.Ordinal))
        {
            configuredPath = "~/" + configuredPath.TrimStart('/', '\\');
        }

        return HttpContext.Current.Server.MapPath(configuredPath);
    }

    private static bool IsExpiredTempDirectory(DirectoryInfo directory, DateTime cutoff)
    {
        DateTime newest = directory.LastWriteTimeUtc;

        foreach (FileInfo file in directory.GetFiles("*", SearchOption.AllDirectories))
        {
            if (file.LastWriteTimeUtc > newest)
            {
                newest = file.LastWriteTimeUtc;
            }
        }

        return newest < cutoff;
    }

    private static bool HasActiveTempLocks(DirectoryInfo directory)
    {
        foreach (FileInfo file in directory.GetFiles("*", SearchOption.AllDirectories))
        {
            if (String.Equals(file.Name, "merge.lock", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(file.Name, "session.lock", StringComparison.OrdinalIgnoreCase) ||
                file.Name.EndsWith(".uploading", StringComparison.OrdinalIgnoreCase))
            {
                if (IsFileLocked(file.FullName))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsFileLocked(string path)
    {
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                return false;
            }
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}

public static class TransferSecurity
{
    public static bool IsAccessTokenEnabled()
    {
        return !String.IsNullOrWhiteSpace(GetConfiguredToken());
    }

    public static bool RequireAuthorized(HttpRequest request, HttpResponse response)
    {
        if (IsAuthorized(request))
        {
            return true;
        }

        response.StatusCode = 401;
        response.ContentType = "text/plain";
        TransferUtility.AddSecurityHeaders(response);
        response.Write("ERROR: Unauthorized. Configure or provide the transfer access token.");
        HttpContext.Current.ApplicationInstance.CompleteRequest();
        return false;
    }

    public static bool IsAuthorized(HttpRequest request)
    {
        string configuredToken = GetConfiguredToken();
        if (String.IsNullOrWhiteSpace(configuredToken))
        {
            return true;
        }

        string providedToken = request.Headers["X-Transfer-Token"];
        if (String.IsNullOrEmpty(providedToken))
        {
            providedToken = request.Form["accessToken"];
        }
        if (String.IsNullOrEmpty(providedToken))
        {
            providedToken = request.QueryString["token"];
        }

        return FixedTimeEquals(configuredToken, providedToken);
    }

    private static string GetConfiguredToken()
    {
        return TransferUtility.GetSetting("TransferAccessToken", "");
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        if (right == null)
        {
            return false;
        }

        byte[] leftBytes = Encoding.UTF8.GetBytes(left);
        byte[] rightBytes = Encoding.UTF8.GetBytes(right);
        int diff = leftBytes.Length ^ rightBytes.Length;
        int length = Math.Max(leftBytes.Length, rightBytes.Length);

        for (int i = 0; i < length; i++)
        {
            byte leftByte = i < leftBytes.Length ? leftBytes[i] : (byte)0;
            byte rightByte = i < rightBytes.Length ? rightBytes[i] : (byte)0;
            diff |= leftByte ^ rightByte;
        }

        return diff == 0;
    }
}
