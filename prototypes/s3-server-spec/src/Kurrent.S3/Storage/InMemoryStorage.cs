using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Kurrent.S3.Storage.Models;

namespace Kurrent.S3.Storage;

/// <summary>
/// In-memory object storage implementation for testing.
/// Thread-safe but not optimized for large datasets.
/// </summary>
public sealed class InMemoryStorage : IObjectStorage, IMultipartUploadManager
{
    readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    readonly ConcurrentDictionary<string, MultipartUploadState> _uploads = new();

    sealed class Bucket
    {
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public ConcurrentDictionary<string, StoredObject> Objects { get; } = new();
    }

    sealed class StoredObject
    {
        public required byte[] Data { get; init; }
        public required string ETag { get; init; }
        public required DateTimeOffset LastModified { get; init; }
        public string? ContentType { get; init; }
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    }

    sealed class MultipartUploadState
    {
        public required string Bucket { get; init; }
        public required string Key { get; init; }
        public required string? ContentType { get; init; }
        public required IReadOnlyDictionary<string, string>? Metadata { get; init; }
        public required DateTimeOffset Initiated { get; init; }
        public ConcurrentDictionary<int, PartState> Parts { get; } = new();
    }

    sealed record PartState(byte[] Data, string ETag, DateTimeOffset LastModified);

    // ==================== Object Operations ====================

    public ValueTask<ObjectMetadata?> GetMetadataAsync(
        string bucket,
        string key,
        CancellationToken ct = default)
    {
        if (!_buckets.TryGetValue(bucket, out var b))
            return ValueTask.FromResult<ObjectMetadata?>(null);

        if (!b.Objects.TryGetValue(key, out var obj))
            return ValueTask.FromResult<ObjectMetadata?>(null);

        return ValueTask.FromResult<ObjectMetadata?>(new ObjectMetadata(
            key,
            obj.Data.Length,
            obj.ETag,
            obj.LastModified,
            obj.ContentType,
            obj.Metadata));
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadObjectAsync(
        string bucket,
        string key,
        long? rangeStart = null,
        long? rangeEnd = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_buckets.TryGetValue(bucket, out var b))
            throw new BucketNotFoundException(bucket);

        if (!b.Objects.TryGetValue(key, out var obj))
            throw new ObjectNotFoundException(bucket, key);

        var data = obj.Data;
        var start = (int)(rangeStart ?? 0);
        var end = (int)(rangeEnd ?? (data.Length - 1));

        if (start < 0) start = 0;
        if (end >= data.Length) end = data.Length - 1;

        // Yield in chunks to simulate streaming
        const int chunkSize = 65536;
        var pos = start;

        while (pos <= end)
        {
            ct.ThrowIfCancellationRequested();

            var remaining = end - pos + 1;
            var toYield = Math.Min(remaining, chunkSize);

            yield return new ReadOnlyMemory<byte>(data, pos, toYield);
            pos += toYield;

            await Task.Yield();
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
        if (!_buckets.TryGetValue(bucket, out var b))
            throw new BucketNotFoundException(bucket);

        // Collect all chunks
        var chunks = new List<byte[]>();
        long totalSize = 0;

        await foreach (var chunk in content.WithCancellation(ct))
        {
            var copy = chunk.ToArray();
            chunks.Add(copy);
            totalSize += copy.Length;
        }

        // Concatenate
        var data = new byte[totalSize];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(data, offset);
            offset += chunk.Length;
        }

        // Compute ETag
        var hash = MD5.HashData(data);
        var etag = $"\"{Convert.ToHexString(hash).ToLowerInvariant()}\"";
        var lastModified = DateTimeOffset.UtcNow;

        var obj = new StoredObject
        {
            Data = data,
            ETag = etag,
            LastModified = lastModified,
            ContentType = contentType,
            Metadata = metadata
        };

        b.Objects[key] = obj;

        return new ObjectMetadata(key, data.Length, etag, lastModified, contentType, metadata);
    }

    public ValueTask<bool> DeleteObjectAsync(
        string bucket,
        string key,
        CancellationToken ct = default)
    {
        if (!_buckets.TryGetValue(bucket, out var b))
            return ValueTask.FromResult(false);

        return ValueTask.FromResult(b.Objects.TryRemove(key, out _));
    }

    // ==================== Listing Operations ====================

    public async IAsyncEnumerable<ObjectInfo> ListObjectsAsync(
        string bucket,
        ListObjectsOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_buckets.TryGetValue(bucket, out var b))
            throw new BucketNotFoundException(bucket);

        var prefix = options.Prefix ?? "";
        var startAfter = options.StartAfter ?? options.ContinuationToken;
        var delimiter = options.Delimiter;
        var count = 0;

        var keys = b.Objects.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal);

        var seenPrefixes = new HashSet<string>();

        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();

            if (startAfter != null && string.CompareOrdinal(key, startAfter) <= 0)
                continue;

            // Handle delimiter
            if (!string.IsNullOrEmpty(delimiter))
            {
                var afterPrefix = key[prefix.Length..];
                var delimiterIndex = afterPrefix.IndexOf(delimiter, StringComparison.Ordinal);

                if (delimiterIndex >= 0)
                {
                    var commonPrefix = prefix + afterPrefix[..(delimiterIndex + delimiter.Length)];
                    seenPrefixes.Add(commonPrefix);
                    continue;
                }
            }

            if (++count > options.MaxKeys)
                yield break;

            var obj = b.Objects[key];
            yield return new ObjectInfo(key, obj.Data.Length, obj.ETag, obj.LastModified);

            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<string> ListCommonPrefixesAsync(
        string bucket,
        string? prefix,
        string delimiter,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_buckets.TryGetValue(bucket, out var b))
            throw new BucketNotFoundException(bucket);

        prefix ??= "";
        var seenPrefixes = new HashSet<string>();

        var keys = b.Objects.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal);

        foreach (var key in keys)
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

    public ValueTask<bool> BucketExistsAsync(string bucket, CancellationToken ct = default) =>
        ValueTask.FromResult(_buckets.ContainsKey(bucket));

    public ValueTask CreateBucketAsync(string bucket, CancellationToken ct = default)
    {
        if (!_buckets.TryAdd(bucket, new Bucket()))
            throw new BucketAlreadyExistsException(bucket);

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteBucketAsync(string bucket, CancellationToken ct = default)
    {
        if (!_buckets.TryGetValue(bucket, out var b))
            throw new BucketNotFoundException(bucket);

        if (!b.Objects.IsEmpty)
            throw new BucketNotEmptyException(bucket);

        _buckets.TryRemove(bucket, out _);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<string> ListBucketsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var name in _buckets.Keys.OrderBy(k => k))
        {
            ct.ThrowIfCancellationRequested();
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
        if (!_buckets.ContainsKey(bucket))
            throw new BucketNotFoundException(bucket);

        var uploadId = Ulid.NewUlid().ToString();

        var state = new MultipartUploadState
        {
            Bucket = bucket,
            Key = key,
            ContentType = contentType,
            Metadata = metadata,
            Initiated = DateTimeOffset.UtcNow
        };

        _uploads[uploadId] = state;
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

        // Collect content
        var chunks = new List<byte[]>();
        await foreach (var chunk in content.WithCancellation(ct))
        {
            chunks.Add(chunk.ToArray());
        }

        var data = new byte[chunks.Sum(c => c.Length)];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(data, offset);
            offset += chunk.Length;
        }

        var hash = MD5.HashData(data);
        var etag = $"\"{Convert.ToHexString(hash).ToLowerInvariant()}\"";
        var lastModified = DateTimeOffset.UtcNow;

        state.Parts[partNumber] = new PartState(data, etag, lastModified);

        return new PartInfo(partNumber, etag, data.Length, lastModified);
    }

    public async IAsyncEnumerable<PartInfo> ListPartsAsync(
        string bucket,
        string key,
        string uploadId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_uploads.TryGetValue(uploadId, out var state))
            throw new NoSuchUploadException(uploadId);

        foreach (var (partNumber, part) in state.Parts.OrderBy(p => p.Key))
        {
            ct.ThrowIfCancellationRequested();
            yield return new PartInfo(partNumber, part.ETag, part.Data.Length, part.LastModified);
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

        var orderedParts = parts.OrderBy(p => p.PartNumber).ToList();

        // Validate parts
        foreach (var part in orderedParts)
        {
            if (!state.Parts.TryGetValue(part.PartNumber, out var stored))
                throw new InvalidPartException(part.PartNumber, $"Part {part.PartNumber} not found");

            if (!string.Equals(stored.ETag, part.ETag, StringComparison.OrdinalIgnoreCase))
                throw new InvalidPartException(part.PartNumber, $"ETag mismatch for part {part.PartNumber}");
        }

        // Concatenate
        async IAsyncEnumerable<ReadOnlyMemory<byte>> GetContent([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var part in orderedParts)
            {
                ct.ThrowIfCancellationRequested();
                yield return state.Parts[part.PartNumber].Data;
            }
        }

        var metadata = await WriteObjectAsync(
            bucket,
            key,
            GetContent(ct),
            contentType: state.ContentType,
            metadata: state.Metadata,
            ct: ct);

        return new CompleteMultipartUploadResult($"/{bucket}/{key}", bucket, key, metadata.ETag);
    }

    public ValueTask AbortUploadAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken ct = default)
    {
        _uploads.TryRemove(uploadId, out _);
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

            yield return new MultipartUploadInfo(state.Key, uploadId, state.Initiated, state.ContentType);
            await Task.Yield();
        }
    }

    // ==================== Test Helpers ====================

    /// <summary>
    /// Clears all data. For testing only.
    /// </summary>
    public void Clear()
    {
        _buckets.Clear();
        _uploads.Clear();
    }

    /// <summary>
    /// Gets total stored bytes across all buckets. For testing only.
    /// </summary>
    public long GetTotalStoredBytes() =>
        _buckets.Values.SelectMany(b => b.Objects.Values).Sum(o => (long)o.Data.Length);
}
