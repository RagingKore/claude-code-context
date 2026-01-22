using System.Runtime.CompilerServices;
using Kurrent.S3.Storage.Models;

namespace Kurrent.S3.Storage;

/// <summary>
/// Manages multipart upload operations.
/// </summary>
public interface IMultipartUploadManager
{
    /// <summary>
    /// Initiates a multipart upload.
    /// </summary>
    /// <returns>Upload ID to use for subsequent operations.</returns>
    ValueTask<string> InitiateUploadAsync(
        string bucket,
        string key,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// Uploads a part.
    /// Parts can be uploaded in any order and in parallel.
    /// </summary>
    /// <param name="bucket">Bucket name.</param>
    /// <param name="key">Object key.</param>
    /// <param name="uploadId">Upload ID from InitiateUpload.</param>
    /// <param name="partNumber">Part number (1 to 10000).</param>
    /// <param name="content">Part content as async enumerable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Part info including ETag.</returns>
    ValueTask<PartInfo> UploadPartAsync(
        string bucket,
        string key,
        string uploadId,
        int partNumber,
        IAsyncEnumerable<ReadOnlyMemory<byte>> content,
        CancellationToken ct = default);

    /// <summary>
    /// Lists uploaded parts for a multipart upload.
    /// </summary>
    IAsyncEnumerable<PartInfo> ListPartsAsync(
        string bucket,
        string key,
        string uploadId,
        [EnumeratorCancellation] CancellationToken ct = default);

    /// <summary>
    /// Completes a multipart upload by assembling the parts.
    /// Parts are concatenated in part number order.
    /// </summary>
    ValueTask<CompleteMultipartUploadResult> CompleteUploadAsync(
        string bucket,
        string key,
        string uploadId,
        IReadOnlyList<CompletedPart> parts,
        CancellationToken ct = default);

    /// <summary>
    /// Aborts a multipart upload and deletes all uploaded parts.
    /// </summary>
    ValueTask AbortUploadAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken ct = default);

    /// <summary>
    /// Lists in-progress multipart uploads for a bucket.
    /// </summary>
    IAsyncEnumerable<MultipartUploadInfo> ListUploadsAsync(
        string bucket,
        string? prefix = null,
        [EnumeratorCancellation] CancellationToken ct = default);
}

/// <summary>
/// Information about an in-progress multipart upload.
/// </summary>
public sealed record MultipartUploadInfo(
    string Key,
    string UploadId,
    DateTimeOffset Initiated,
    string? ContentType = null);

// ==================== Exceptions ====================

public class NoSuchUploadException : S3StorageException
{
    public string UploadId { get; }

    public NoSuchUploadException(string uploadId)
        : base("NoSuchUpload", $"The specified upload does not exist: {uploadId}", 404)
    {
        UploadId = uploadId;
    }
}

public class InvalidPartException : S3StorageException
{
    public int PartNumber { get; }

    public InvalidPartException(int partNumber, string message)
        : base("InvalidPart", message, 400)
    {
        PartNumber = partNumber;
    }
}

public class InvalidPartOrderException : S3StorageException
{
    public InvalidPartOrderException()
        : base("InvalidPartOrder", "Parts were not in ascending order", 400)
    {
    }
}

public class EntityTooSmallException : S3StorageException
{
    public int PartNumber { get; }

    public EntityTooSmallException(int partNumber)
        : base("EntityTooSmall", $"Part {partNumber} is smaller than the minimum allowed size (5MB except for the last part)", 400)
    {
        PartNumber = partNumber;
    }
}
