# File Transfer Tool Agent Guide

This repository is a long-lived collaborative project. Make narrowly scoped, reviewable changes and preserve the existing deployment model: ASP.NET Web Forms on IIS with no database dependency.

## Repository Map

- `default.aspx`: browser UI, including the JavaScript chunk-upload client.
- `upload_chunk.aspx.cs`: validated chunk ingestion, resume behavior, temporary merge, SHA-256 generation, and timestamp application.
- `upload.aspx.cs`: legacy single-request upload fallback. Do not add large-file behavior here without a clear compatibility reason.
- `download.aspx.cs`, `delete.aspx.cs`: download and deletion endpoints.
- `App_Code/TransferUtility.cs`: shared configuration, authorization, path handling, cleanup, and response helpers.
- `Global.asax`: application-lifetime temporary-upload cleanup timer.
- `web.config`: operational configuration and IIS request limits.

## Non-Negotiable Transfer Rules

1. All browser uploads must remain chunked through `upload_chunk.aspx`; a download is streamed directly and is not chunked unless an explicit range/resume feature is implemented.
2. Write incoming chunks and merged files only beneath `TransferTempRoot`. Merge completely, validate the expected byte count, close the file, then move it into `TransferStorageRoot`.
3. Never expose partial chunks or `.merging` files in the visible storage root. A failed or interrupted upload must remain resumable until expiry, then be removed by cleanup.
4. Treat every request field as untrusted. Continue using `TransferUtility` for upload IDs, groups, file names, physical paths, integer parsing, authorization, and response headers.
5. Do not weaken access-token checks, path normalization, request validation, or security headers to make a feature work.

## Size, Speed, and Configuration

- `TransferMaxChunkBytes` is in bytes and drives the browser chunk size. Its default is 256 MiB (`268435456`).
- `httpRuntime.maxRequestLength` is KiB; IIS `requestLimits.maxAllowedContentLength` is bytes. Both must exceed the chunk size plus multipart overhead.
- `TransferMaxFileBytes=0` means no application-level total-file limit. Disk capacity, filesystem, proxy, and IIS limits still apply.
- `TransferParallelUploads` controls concurrent file uploads, not concurrent chunks of one file. Change it conservatively because each active transfer consumes disk I/O and IIS workers.
- Keep `TransferTempMaxAgeMinutes` and `TransferTempCleanupIntervalMinutes` aligned with the expected resume window. Cleanup must be best-effort and must not fail active requests.

## Metadata and Compatibility

- The browser provides `File.lastModified`; preserve it as the stored file's UTC modification time.
- Standard browser file inputs cannot access the original creation or access timestamps. Custom clients may send `createdUtc`/`creationTimeUtc` and `lastAccessedUtc`/`lastAccessTimeUtc` as ISO-8601 UTC values or Unix milliseconds.
- All chunks for an upload must carry identical immutable metadata. Reject conflicts rather than silently accepting mixed files.
- Avoid APIs that require a newer runtime than .NET Framework 4.8. Keep server code compatible with the existing C# Web Forms compilation environment.

## Implementation Conventions

- Prefer simple BCL APIs and explicit validation over new dependencies or framework rewrites.
- Use `long` for byte counts, file sizes, offsets, and request-derived size values. Do not reintroduce 32-bit total-size assumptions.
- Stream files with bounded buffers; never load an upload or download into memory in one allocation.
- Maintain atomicity: write to a uniquely named temporary file, flush and close it, then move it to the final path.
- Error responses must not reveal physical paths, tokens, stack traces, or internal configuration.
- Keep comments short and only where they explain an invariant, failure mode, or compatibility constraint.

## Verification Before Commit

Run the checks appropriate to the files changed. The baseline server-side validation is:

```sh
mcs -target:library -r:System.Web.dll -r:System.Configuration.dll \\
  -out:/tmp/file-transfer-tool-check.dll \\
  App_Code/TransferUtility.cs upload.aspx.cs upload_chunk.aspx.cs \\
  delete.aspx.cs download.aspx.cs
xmllint --noout web.config
git diff --check
```

For upload-protocol changes, also manually verify at least these cases on IIS:

- a file larger than 4 GiB, when storage and network capacity permit;
- an interrupted upload that resumes without overwriting a completed chunk;
- expiry cleanup of an abandoned temporary upload;
- a completed file is absent from the visible root until the merge succeeds;
- file modification time is preserved by the browser chunk-upload path;
- unauthorized requests and traversal-shaped file names are rejected.

## Git and Pull Requests

- Start new work from the current upstream `origin/main` unless it is intentionally extending an open PR branch.
- Use `codex/<short-description>` for new branches. Preserve existing branch names when continuing existing PRs.
- Do not overwrite, reset, or discard user changes. Keep unrelated changes intact.
- Keep commits focused. Include tests or validation evidence in the commit/PR description.
- Pull-request bodies for this project must begin exactly with: `我是 dazi 的 agent。`
- State user-visible behavior, deployment/configuration impact, security implications, and validation performed in every PR.
