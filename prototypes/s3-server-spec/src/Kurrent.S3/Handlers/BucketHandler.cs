using Kurrent.S3.Storage;
using Kurrent.S3.Xml;
using Microsoft.Extensions.Logging;

namespace Kurrent.S3.Handlers;

/// <summary>
/// Handles bucket-level S3 operations.
/// </summary>
public sealed class BucketHandler
{
    readonly IObjectStorage _storage;
    readonly ILogger<BucketHandler> _logger;

    public BucketHandler(IObjectStorage storage, ILogger<BucketHandler> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    /// <summary>
    /// HEAD /{bucket} - Check if bucket exists.
    /// </summary>
    public async Task<IResult> HeadBucketAsync(string bucket, CancellationToken ct)
    {
        _logger.LogDebug("HeadBucket: {Bucket}", bucket);

        var exists = await _storage.BucketExistsAsync(bucket, ct);

        if (!exists)
            return S3Results.NotFound("NoSuchBucket", $"The specified bucket does not exist: {bucket}");

        return Results.Ok();
    }

    /// <summary>
    /// PUT /{bucket} - Create bucket.
    /// </summary>
    public async Task<IResult> CreateBucketAsync(string bucket, CancellationToken ct)
    {
        _logger.LogDebug("CreateBucket: {Bucket}", bucket);

        try
        {
            await _storage.CreateBucketAsync(bucket, ct);
            return Results.Ok();
        }
        catch (BucketAlreadyExistsException ex)
        {
            return S3Results.Error(ex, 409);
        }
    }

    /// <summary>
    /// DELETE /{bucket} - Delete bucket.
    /// </summary>
    public async Task<IResult> DeleteBucketAsync(string bucket, CancellationToken ct)
    {
        _logger.LogDebug("DeleteBucket: {Bucket}", bucket);

        try
        {
            await _storage.DeleteBucketAsync(bucket, ct);
            return Results.NoContent();
        }
        catch (BucketNotFoundException ex)
        {
            return S3Results.Error(ex, 404);
        }
        catch (BucketNotEmptyException ex)
        {
            return S3Results.Error(ex, 409);
        }
    }

    /// <summary>
    /// GET / - List all buckets.
    /// </summary>
    public async Task<IResult> ListBucketsAsync(CancellationToken ct)
    {
        _logger.LogDebug("ListBuckets");

        var buckets = new List<(string Name, DateTimeOffset CreationDate)>();

        await foreach (var name in _storage.ListBucketsAsync(ct))
        {
            // TODO: Store actual creation date
            buckets.Add((name, DateTimeOffset.UtcNow));
        }

        var xml = S3XmlWriter.WriteListAllMyBucketsResult(buckets);
        return Results.Content(xml, "application/xml");
    }
}
