using System.Runtime.CompilerServices;
using Kurrent.S3.Storage.Models;

namespace Kurrent.S3.Storage;

/// <summary>
/// Abstraction for object storage operations.
/// Implementations should be thread-safe.
/// </summary>
public interface IObjectStorage
{
    // ==================== Object Operations ====================

    /// <summary>
    /// Gets metadata for an object without retrieving its content.
    /// </summary>
    /// <returns>Object metadata if exists, null otherwise.</returns>
    ValueTask<ObjectMetadata?> GetMetadataAsync(
        string bucket,
        string key,
        CancellationToken ct = default);

    /// <summary>
    /// Reads object content as an async enumerable of chunks.
    /// Supports range requests for partial reads.
    /// </summary>
    /// <param name="bucket">Bucket name.</param>
    /// <param name="key">Object key.</param>
    /// <param name="rangeStart">Start byte offset (inclusive), null for beginning.</param>
    /// <param name="rangeEnd">End byte offset (inclusive), null for end of file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of content chunks.</returns>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadObjectAsync(
        string bucket,
        string key,
        long? rangeStart = null,
        long? rangeEnd = null,
        [EnumeratorCancellation] CancellationToken ct = default);

    /// <summary>
    /// Writes an object from an async enumerable of chunks.
    /// Uses incremental hashing to compute ETag without buffering.
    /// </summary>
    ValueTask<ObjectMetadata> WriteObjectAsync(
        string bucket,
        string key,
        IAsyncEnumerable<ReadOnlyMemory<byte>> content,
        long? contentLength = null,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes an object.
    /// </summary>
    /// <returns>True if object was deleted, false if it didn't exist.</returns>
    ValueTask<bool> DeleteObjectAsync(
        string bucket,
        string key,
        CancellationToken ct = default);

    // ==================== Listing Operations ====================

    /// <summary>
    /// Lists objects in a bucket with optional filtering.
    /// Supports prefix, delimiter, continuation token for pagination.
    /// </summary>
    IAsyncEnumerable<ObjectInfo> ListObjectsAsync(
        string bucket,
        ListObjectsOptions options,
        [EnumeratorCancellation] CancellationToken ct = default);

    /// <summary>
    /// Gets common prefixes (virtual directories) for a listing.
    /// Only applicable when delimiter is specified.
    /// </summary>
    IAsyncEnumerable<string> ListCommonPrefixesAsync(
        string bucket,
        string? prefix,
        string delimiter,
        [EnumeratorCancellation] CancellationToken ct = default);

    // ==================== Bucket Operations ====================

    /// <summary>
    /// Checks if a bucket exists.
    /// </summary>
    ValueTask<bool> BucketExistsAsync(
        string bucket,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a bucket.
    /// </summary>
    /// <exception cref="BucketAlreadyExistsException">If bucket already exists.</exception>
    ValueTask CreateBucketAsync(
        string bucket,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a bucket.
    /// </summary>
    /// <exception cref="BucketNotEmptyException">If bucket is not empty.</exception>
    /// <exception cref="BucketNotFoundException">If bucket doesn't exist.</exception>
    ValueTask DeleteBucketAsync(
        string bucket,
        CancellationToken ct = default);

    /// <summary>
    /// Lists all buckets.
    /// </summary>
    IAsyncEnumerable<string> ListBucketsAsync(
        [EnumeratorCancellation] CancellationToken ct = default);
}

// ==================== Exceptions ====================

public class S3StorageException : Exception
{
    public string ErrorCode { get; }
    public int HttpStatusCode { get; }

    public S3StorageException(string errorCode, string message, int httpStatusCode)
        : base(message)
    {
        ErrorCode = errorCode;
        HttpStatusCode = httpStatusCode;
    }
}

public class BucketNotFoundException : S3StorageException
{
    public string Bucket { get; }

    public BucketNotFoundException(string bucket)
        : base("NoSuchBucket", $"The specified bucket does not exist: {bucket}", 404)
    {
        Bucket = bucket;
    }
}

public class BucketAlreadyExistsException : S3StorageException
{
    public string Bucket { get; }

    public BucketAlreadyExistsException(string bucket)
        : base("BucketAlreadyExists", $"The bucket already exists: {bucket}", 409)
    {
        Bucket = bucket;
    }
}

public class BucketNotEmptyException : S3StorageException
{
    public string Bucket { get; }

    public BucketNotEmptyException(string bucket)
        : base("BucketNotEmpty", $"The bucket is not empty: {bucket}", 409)
    {
        Bucket = bucket;
    }
}

public class ObjectNotFoundException : S3StorageException
{
    public string Bucket { get; }
    public string Key { get; }

    public ObjectNotFoundException(string bucket, string key)
        : base("NoSuchKey", $"The specified key does not exist: {key}", 404)
    {
        Bucket = bucket;
        Key = key;
    }
}
