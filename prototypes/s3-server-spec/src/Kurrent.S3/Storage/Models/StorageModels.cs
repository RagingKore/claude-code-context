namespace Kurrent.S3.Storage.Models;

/// <summary>
/// Metadata for a stored object.
/// </summary>
public readonly record struct ObjectMetadata(
    string Key,
    long Size,
    string ETag,
    DateTimeOffset LastModified,
    string? ContentType = null,
    IReadOnlyDictionary<string, string>? UserMetadata = null);

/// <summary>
/// Lightweight object info for listings.
/// </summary>
public readonly record struct ObjectInfo(
    string Key,
    long Size,
    string ETag,
    DateTimeOffset LastModified);

/// <summary>
/// Options for ListObjectsV2 operation.
/// </summary>
public sealed record ListObjectsOptions
{
    public string? Prefix { get; init; }
    public string? Delimiter { get; init; }
    public string? StartAfter { get; init; }
    public string? ContinuationToken { get; init; }
    public int MaxKeys { get; init; } = 1000;
}

/// <summary>
/// Result of a list objects operation.
/// </summary>
public sealed record ListObjectsResult
{
    public required string Name { get; init; }
    public string? Prefix { get; init; }
    public string? Delimiter { get; init; }
    public int MaxKeys { get; init; }
    public bool IsTruncated { get; init; }
    public string? NextContinuationToken { get; init; }
    public string? ContinuationToken { get; init; }
    public int KeyCount { get; init; }
    public required IReadOnlyList<ObjectInfo> Contents { get; init; }
    public IReadOnlyList<string>? CommonPrefixes { get; init; }
}

/// <summary>
/// Information about an uploaded part.
/// </summary>
public readonly record struct PartInfo(int PartNumber, string ETag, long Size, DateTimeOffset LastModified);

/// <summary>
/// Reference to a completed part for CompleteMultipartUpload.
/// </summary>
public readonly record struct CompletedPart(int PartNumber, string ETag);

/// <summary>
/// Result of initiating a multipart upload.
/// </summary>
public sealed record InitiateMultipartUploadResult(string Bucket, string Key, string UploadId);

/// <summary>
/// Result of completing a multipart upload.
/// </summary>
public sealed record CompleteMultipartUploadResult(
    string Location,
    string Bucket,
    string Key,
    string ETag);
