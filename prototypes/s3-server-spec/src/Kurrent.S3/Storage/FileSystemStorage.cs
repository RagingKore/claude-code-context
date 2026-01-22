using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Kurrent.S3.Storage.Models;
using Microsoft.Extensions.Logging;

namespace Kurrent.S3.Storage;

/// <summary>
/// File system based object storage implementation.
/// Objects are stored as files, buckets as directories.
/// </summary>
public sealed class FileSystemStorage : IObjectStorage, IMultipartUploadManager
{
    readonly string _rootPath;
    readonly string _uploadsPath;
    readonly ILogger<FileSystemStorage> _logger;
    readonly ConcurrentDictionary<string, MultipartUploadState> _uploads = new();

    const int DefaultBufferSize = 81920; // 80KB chunks

    sealed class MultipartUploadState
    {
        public required string Bucket { get; init; }
        public required string Key { get; init; }
        public required string? ContentType { get; init; }
        public required IReadOnlyDictionary<string, string>? Metadata { get; init; }
        public required DateTimeOffset Initiated { get; init; }
        public required string UploadDirectory { get; init; }
        public ConcurrentDictionary<int, PartState> Parts { get; } = new();
    }

    sealed record PartState(string FilePath, long Size, string ETag, DateTimeOffset LastModified);

    public FileSystemStorage(string rootPath, ILogger<FileSystemStorage> logger)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _uploadsPath = Path.Combine(_rootPath, ".uploads");
        _logger = logger;

        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(_uploadsPath);
    }

    // ==================== Object Operations ====================

    public ValueTask<ObjectMetadata?> GetMetadataAsync(
        string bucket,
        string key,
        CancellationToken ct = default)
    {
        var path = GetObjectPath(bucket, key);

        if (!File.Exists(path))
            return ValueTask.FromResult<ObjectMetadata?>(null);

        var info = new FileInfo(path);
        var etag = ComputeETagFromFile(path);

        return ValueTask.FromResult<ObjectMetadata?>(new ObjectMetadata(
            Key: key,
            Size: info.Length,
            ETag: etag,
            LastModified: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            ContentType: GuessContentType(key)));
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadObjectAsync(
        string bucket,
        string key,
        long? rangeStart = null,
        long? rangeEnd = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = GetObjectPath(bucket, key);

        if (!File.Exists(path))
            throw new ObjectNotFoundException(bucket, key);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var fileLength = stream.Length;
        var start = rangeStart ?? 0;
        var end = rangeEnd ?? (fileLength - 1);

        // Validate range
        if (start < 0) start = 0;
        if (end >= fileLength) end = fileLength - 1;
        if (start > end) yield break;

        stream.Position = start;
        var remaining = end - start + 1;

        using var bufferOwner = MemoryPool<byte>.Shared.Rent(DefaultBufferSize);
        var buffer = bufferOwner.Memory;

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();

            var toRead = (int)Math.Min(remaining, buffer.Length);
            var bytesRead = await stream.ReadAsync(buffer[..toRead], ct);

            if (bytesRead == 0)
                yield break;

            remaining -= bytesRead;

            // Return a copy since we reuse the buffer
            var chunk = new byte[bytesRead];
            buffer[..bytesRead].CopyTo(chunk);
            yield return chunk;
        }
    }

    public async ValueTask<ObjectMetadata> WriteObjectAsync(
        string bucket,
        string key,
        IAsyncEnumerable<ReadOnlyMemory<byte>> content,
        long? contentLength = null,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        var bucketPath = GetBucketPath(bucket);
        if (!Directory.Exists(bucketPath))
            throw new BucketNotFoundException(bucket);

        var path = GetObjectPath(bucket, key);
        var directory = Path.GetDirectoryName(path);
        if (directory != null)
            Directory.CreateDirectory(directory);

        var tempPath = path + $".tmp.{Guid.NewGuid():N}";
        long totalSize = 0;

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await foreach (var chunk in content.WithCancellation(ct))
                {
                    md5.AppendData(chunk.Span);
                    await stream.WriteAsync(chunk, ct);
                    totalSize += chunk.Length;
                }
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }

        var etag = $"\"{Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant()}\"";
        var lastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);

        _logger.LogDebug(
            "Wrote object {Bucket}/{Key}: {Size} bytes, ETag={ETag}",
            bucket, key, totalSize, etag);

        return new ObjectMetadata(key, totalSize, etag, lastModified, contentType ?? GuessContentType(key), metadata);
    }

    public ValueTask<bool> DeleteObjectAsync(
        string bucket,
        string key,
        CancellationToken ct = default)
    {
        var path = GetObjectPath(bucket, key);

        if (!File.Exists(path))
            return ValueTask.FromResult(false);

        File.Delete(path);

        _logger.LogDebug("Deleted object {Bucket}/{Key}", bucket, key);

        return ValueTask.FromResult(true);
    }

    // ==================== Listing Operations ====================

    public async IAsyncEnumerable<ObjectInfo> ListObjectsAsync(
        string bucket,
        ListObjectsOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var bucketPath = GetBucketPath(bucket);
        if (!Directory.Exists(bucketPath))
            throw new BucketNotFoundException(bucket);

        var prefix = options.Prefix ?? "";
        var startAfter = options.StartAfter ?? options.ContinuationToken;
        var hasDelimiter = !string.IsNullOrEmpty(options.Delimiter);
        var delimiter = options.Delimiter ?? "";
        var count = 0;

        // Get all files under the bucket
        var searchPattern = "*";
        var files = Directory.EnumerateFiles(bucketPath, searchPattern, SearchOption.AllDirectories)
            .Select(f => GetKeyFromPath(bucketPath, f))
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal);

        var seenPrefixes = new HashSet<string>();

        foreach (var key in files)
        {
            ct.ThrowIfCancellationRequested();

            // Skip until we pass the continuation token
            if (startAfter != null && string.CompareOrdinal(key, startAfter) <= 0)
                continue;

            // Handle delimiter - group by common prefix
            if (hasDelimiter)
            {
                var afterPrefix = key[prefix.Length..];
                var delimiterIndex = afterPrefix.IndexOf(delimiter, StringComparison.Ordinal);

                if (delimiterIndex >= 0)
                {
                    // This key has a delimiter, skip it (will be returned as common prefix)
                    var commonPrefix = prefix + afterPrefix[..(delimiterIndex + delimiter.Length)];
                    seenPrefixes.Add(commonPrefix);
                    continue;
                }
            }

            if (++count > options.MaxKeys)
                yield break;

            var filePath = Path.Combine(bucketPath, key.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(filePath);
            var etag = ComputeETagFromFile(filePath);

            yield return new ObjectInfo(
                key,
                info.Length,
                etag,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));

            // Yield to allow other work
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<string> ListCommonPrefixesAsync(
        string bucket,
        string? prefix,
        string delimiter,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var bucketPath = GetBucketPath(bucket);
        if (!Directory.Exists(bucketPath))
            throw new BucketNotFoundException(bucket);

        prefix ??= "";
        var seenPrefixes = new HashSet<string>();

        var files = Directory.EnumerateFiles(bucketPath, "*", SearchOption.AllDirectories)
            .Select(f => GetKeyFromPath(bucketPath, f))
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal);

        foreach (var key in files)
        {
            ct.ThrowIfCancellationRequested();

            var afterPrefix = key[prefix.Length..];
            var delimiterIndex = afterPrefix.IndexOf(delimiter, StringComparison.Ordinal);

            if (delimiterIndex >= 0)
            {
                var commonPrefix = prefix + afterPrefix[..(delimiterIndex + delimiter.Length)];

                if (seenPrefixes.Add(commonPrefix))
                {
                    yield return commonPrefix;
                    await Task.Yield();
                }
            }
        }
    }

    // ==================== Bucket Operations ====================

    public ValueTask<bool> BucketExistsAsync(string bucket, CancellationToken ct = default)
    {
        var path = GetBucketPath(bucket);
        return ValueTask.FromResult(Directory.Exists(path));
    }

    public ValueTask CreateBucketAsync(string bucket, CancellationToken ct = default)
    {
        var path = GetBucketPath(bucket);

        if (Directory.Exists(path))
            throw new BucketAlreadyExistsException(bucket);

        Directory.CreateDirectory(path);
        _logger.LogInformation("Created bucket {Bucket}", bucket);

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteBucketAsync(string bucket, CancellationToken ct = default)
    {
        var path = GetBucketPath(bucket);

        if (!Directory.Exists(path))
            throw new BucketNotFoundException(bucket);

        var hasFiles = Directory.EnumerateFileSystemEntries(path).Any();
        if (hasFiles)
            throw new BucketNotEmptyException(bucket);

        Directory.Delete(path);
        _logger.LogInformation("Deleted bucket {Bucket}", bucket);

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<string> ListBucketsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var dir in Directory.EnumerateDirectories(_rootPath))
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(dir);

            // Skip internal directories
            if (name.StartsWith('.'))
                continue;

            yield return name;
            await Task.Yield();
        }
    }

    // ==================== Multipart Upload Operations ====================

    public ValueTask<string> InitiateUploadAsync(
        string bucket,
        string key,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        var bucketPath = GetBucketPath(bucket);
        if (!Directory.Exists(bucketPath))
            throw new BucketNotFoundException(bucket);

        var uploadId = Ulid.NewUlid().ToString();
        var uploadDir = Path.Combine(_uploadsPath, uploadId);
        Directory.CreateDirectory(uploadDir);

        var state = new MultipartUploadState
        {
            Bucket = bucket,
            Key = key,
            ContentType = contentType,
            Metadata = metadata,
            Initiated = DateTimeOffset.UtcNow,
            UploadDirectory = uploadDir
        };

        _uploads[uploadId] = state;

        _logger.LogDebug(
            "Initiated multipart upload {UploadId} for {Bucket}/{Key}",
            uploadId, bucket, key);

        return ValueTask.FromResult(uploadId);
    }

    public async ValueTask<PartInfo> UploadPartAsync(
        string bucket,
        string key,
        string uploadId,
        int partNumber,
        IAsyncEnumerable<ReadOnlyMemory<byte>> content,
        CancellationToken ct = default)
    {
        if (!_uploads.TryGetValue(uploadId, out var state))
            throw new NoSuchUploadException(uploadId);

        if (state.Bucket != bucket || state.Key != key)
            throw new NoSuchUploadException(uploadId);

        if (partNumber < 1 || partNumber > 10000)
            throw new InvalidPartException(partNumber, $"Part number must be between 1 and 10000, got {partNumber}");

        var partPath = Path.Combine(state.UploadDirectory, $"part-{partNumber:D5}");
        long size = 0;

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

        await using (var stream = new FileStream(
            partPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await foreach (var chunk in content.WithCancellation(ct))
            {
                md5.AppendData(chunk.Span);
                await stream.WriteAsync(chunk, ct);
                size += chunk.Length;
            }
        }

        var etag = $"\"{Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant()}\"";
        var lastModified = DateTimeOffset.UtcNow;

        var partState = new PartState(partPath, size, etag, lastModified);
        state.Parts[partNumber] = partState;

        _logger.LogDebug(
            "Uploaded part {PartNumber} for {UploadId}: {Size} bytes, ETag={ETag}",
            partNumber, uploadId, size, etag);

        return new PartInfo(partNumber, etag, size, lastModified);
    }

    public async IAsyncEnumerable<PartInfo> ListPartsAsync(
        string bucket,
        string key,
        string uploadId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_uploads.TryGetValue(uploadId, out var state))
            throw new NoSuchUploadException(uploadId);

        foreach (var (partNumber, partState) in state.Parts.OrderBy(p => p.Key))
        {
            ct.ThrowIfCancellationRequested();
            yield return new PartInfo(partNumber, partState.ETag, partState.Size, partState.LastModified);
            await Task.Yield();
        }
    }

    public async ValueTask<CompleteMultipartUploadResult> CompleteUploadAsync(
        string bucket,
        string key,
        string uploadId,
        IReadOnlyList<CompletedPart> parts,
        CancellationToken ct = default)
    {
        if (!_uploads.TryRemove(uploadId, out var state))
            throw new NoSuchUploadException(uploadId);

        try
        {
            // Validate parts
            var orderedParts = parts.OrderBy(p => p.PartNumber).ToList();

            // Check ascending order
            for (var i = 1; i < orderedParts.Count; i++)
            {
                if (orderedParts[i].PartNumber <= orderedParts[i - 1].PartNumber)
                    throw new InvalidPartOrderException();
            }

            // Validate each part exists with matching ETag
            foreach (var part in orderedParts)
            {
                if (!state.Parts.TryGetValue(part.PartNumber, out var stored))
                    throw new InvalidPartException(part.PartNumber, $"Part {part.PartNumber} not found");

                if (!string.Equals(stored.ETag, part.ETag, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidPartException(part.PartNumber, $"ETag mismatch for part {part.PartNumber}");

                // Check minimum size (5MB) except for last part
                if (part != orderedParts[^1] && stored.Size < 5 * 1024 * 1024)
                    throw new EntityTooSmallException(part.PartNumber);
            }

            // Concatenate parts
            async IAsyncEnumerable<ReadOnlyMemory<byte>> ConcatenateParts(
                [EnumeratorCancellation] CancellationToken ct)
            {
                foreach (var part in orderedParts)
                {
                    var partState = state.Parts[part.PartNumber];
                    await using var stream = new FileStream(
                        partState.FilePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 4096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);

                    using var bufferOwner = MemoryPool<byte>.Shared.Rent(DefaultBufferSize);
                    var buffer = bufferOwner.Memory;

                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
                    {
                        var chunk = new byte[bytesRead];
                        buffer[..bytesRead].CopyTo(chunk);
                        yield return chunk;
                    }
                }
            }

            var metadata = await WriteObjectAsync(
                bucket,
                key,
                ConcatenateParts(ct),
                contentType: state.ContentType,
                metadata: state.Metadata,
                ct: ct);

            _logger.LogDebug(
                "Completed multipart upload {UploadId}: {Bucket}/{Key}, {Size} bytes",
                uploadId, bucket, key, metadata.Size);

            return new CompleteMultipartUploadResult(
                Location: $"/{bucket}/{key}",
                Bucket: bucket,
                Key: key,
                ETag: metadata.ETag);
        }
        finally
        {
            // Cleanup upload directory
            try
            {
                if (Directory.Exists(state.UploadDirectory))
                    Directory.Delete(state.UploadDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup upload directory {Dir}", state.UploadDirectory);
            }
        }
    }

    public ValueTask AbortUploadAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken ct = default)
    {
        if (!_uploads.TryRemove(uploadId, out var state))
            throw new NoSuchUploadException(uploadId);

        try
        {
            if (Directory.Exists(state.UploadDirectory))
                Directory.Delete(state.UploadDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup upload directory {Dir}", state.UploadDirectory);
        }

        _logger.LogDebug("Aborted multipart upload {UploadId}", uploadId);

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<MultipartUploadInfo> ListUploadsAsync(
        string bucket,
        string? prefix = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var (uploadId, state) in _uploads)
        {
            ct.ThrowIfCancellationRequested();

            if (state.Bucket != bucket)
                continue;

            if (prefix != null && !state.Key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            yield return new MultipartUploadInfo(
                state.Key,
                uploadId,
                state.Initiated,
                state.ContentType);

            await Task.Yield();
        }
    }

    // ==================== Helper Methods ====================

    string GetBucketPath(string bucket) =>
        Path.Combine(_rootPath, SanitizeName(bucket));

    string GetObjectPath(string bucket, string key)
    {
        var bucketPath = GetBucketPath(bucket);
        var sanitizedKey = key.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(bucketPath, sanitizedKey);
    }

    static string GetKeyFromPath(string bucketPath, string filePath)
    {
        var relativePath = Path.GetRelativePath(bucketPath, filePath);
        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    static string SanitizeName(string name) =>
        name.Replace("..", "_").Replace('/', '_').Replace('\\', '_');

    static string ComputeETagFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(stream);
        return $"\"{Convert.ToHexString(hash).ToLowerInvariant()}\"";
    }

    static string? GuessContentType(string key) => Path.GetExtension(key).ToLowerInvariant() switch
    {
        ".parquet" => "application/vnd.apache.parquet",
        ".json" => "application/json",
        ".csv" => "text/csv",
        ".txt" => "text/plain",
        ".xml" => "application/xml",
        ".html" => "text/html",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".gz" => "application/gzip",
        _ => "application/octet-stream"
    };
}
