using System.Buffers;
using System.Runtime.CompilerServices;
using Kurrent.S3.Storage;
using Kurrent.S3.Storage.Models;
using Kurrent.S3.Xml;
using Microsoft.Extensions.Logging;

namespace Kurrent.S3.Handlers;

/// <summary>
/// Handles object-level S3 operations.
/// </summary>
public sealed class ObjectHandler
{
    readonly IObjectStorage _storage;
    readonly ILogger<ObjectHandler> _logger;

    public ObjectHandler(IObjectStorage storage, ILogger<ObjectHandler> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    /// <summary>
    /// HEAD /{bucket}/{key} - Get object metadata.
    /// </summary>
    public async Task<IResult> HeadObjectAsync(string bucket, string key, HttpContext ctx, CancellationToken ct)
    {
        _logger.LogDebug("HeadObject: {Bucket}/{Key}", bucket, key);

        var metadata = await _storage.GetMetadataAsync(bucket, key, ct);

        if (metadata == null)
            return S3Results.NotFound("NoSuchKey", $"The specified key does not exist: {key}");

        ctx.Response.Headers.ContentLength = metadata.Value.Size;
        ctx.Response.Headers.ETag = metadata.Value.ETag;
        ctx.Response.Headers["Last-Modified"] = metadata.Value.LastModified.ToString("R");

        if (metadata.Value.ContentType != null)
            ctx.Response.ContentType = metadata.Value.ContentType;

        return Results.Ok();
    }

    /// <summary>
    /// GET /{bucket}/{key} - Get object.
    /// </summary>
    public async Task GetObjectAsync(string bucket, string key, HttpContext ctx, CancellationToken ct)
    {
        _logger.LogDebug("GetObject: {Bucket}/{Key}", bucket, key);

        var metadata = await _storage.GetMetadataAsync(bucket, key, ct);

        if (metadata == null)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentType = "application/xml";
            await ctx.Response.WriteAsync(
                S3XmlWriter.WriteError("NoSuchKey", $"The specified key does not exist: {key}"),
                ct);
            return;
        }

        // Parse range header
        long? rangeStart = null;
        long? rangeEnd = null;

        if (ctx.Request.Headers.TryGetValue("Range", out var rangeHeader))
        {
            var range = rangeHeader.ToString();
            if (range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var parts = range[6..].Split('-');
                if (parts.Length == 2)
                {
                    if (!string.IsNullOrEmpty(parts[0]) && long.TryParse(parts[0], out var start))
                        rangeStart = start;

                    if (!string.IsNullOrEmpty(parts[1]) && long.TryParse(parts[1], out var end))
                        rangeEnd = end;

                    // Handle suffix range (e.g., bytes=-500)
                    if (rangeStart == null && rangeEnd != null)
                    {
                        rangeStart = metadata.Value.Size - rangeEnd.Value;
                        rangeEnd = metadata.Value.Size - 1;
                    }
                }
            }
        }

        // Set response headers
        ctx.Response.Headers.ETag = metadata.Value.ETag;
        ctx.Response.Headers["Last-Modified"] = metadata.Value.LastModified.ToString("R");
        ctx.Response.Headers["Accept-Ranges"] = "bytes";

        if (metadata.Value.ContentType != null)
            ctx.Response.ContentType = metadata.Value.ContentType;
        else
            ctx.Response.ContentType = "application/octet-stream";

        if (rangeStart != null || rangeEnd != null)
        {
            ctx.Response.StatusCode = 206;
            var actualStart = rangeStart ?? 0;
            var actualEnd = rangeEnd ?? (metadata.Value.Size - 1);
            ctx.Response.Headers.ContentLength = actualEnd - actualStart + 1;
            ctx.Response.Headers["Content-Range"] = $"bytes {actualStart}-{actualEnd}/{metadata.Value.Size}";
        }
        else
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.ContentLength = metadata.Value.Size;
        }

        // Stream content
        try
        {
            await foreach (var chunk in _storage.ReadObjectAsync(bucket, key, rangeStart, rangeEnd, ct))
            {
                await ctx.Response.Body.WriteAsync(chunk, ct);
            }
        }
        catch (ObjectNotFoundException)
        {
            // Object was deleted between metadata check and read
            ctx.Response.StatusCode = 404;
        }
    }

    /// <summary>
    /// PUT /{bucket}/{key} - Put object.
    /// </summary>
    public async Task<IResult> PutObjectAsync(
        string bucket,
        string key,
        HttpRequest request,
        CancellationToken ct)
    {
        _logger.LogDebug("PutObject: {Bucket}/{Key}", bucket, key);

        var contentLength = request.ContentLength;
        var contentType = request.ContentType;

        async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadRequestBody(
            [EnumeratorCancellation] CancellationToken ct)
        {
            using var bufferOwner = MemoryPool<byte>.Shared.Rent(81920);
            var buffer = bufferOwner.Memory;

            int bytesRead;
            while ((bytesRead = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                // Return a copy since we reuse the buffer
                var chunk = new byte[bytesRead];
                buffer[..bytesRead].CopyTo(chunk);
                yield return chunk;
            }
        }

        try
        {
            var metadata = await _storage.WriteObjectAsync(
                bucket,
                key,
                ReadRequestBody(ct),
                contentLength,
                contentType,
                ct: ct);

            return Results.Ok(new { ETag = metadata.ETag });
        }
        catch (BucketNotFoundException ex)
        {
            return S3Results.Error(ex, 404);
        }
    }

    /// <summary>
    /// DELETE /{bucket}/{key} - Delete object.
    /// </summary>
    public async Task<IResult> DeleteObjectAsync(string bucket, string key, CancellationToken ct)
    {
        _logger.LogDebug("DeleteObject: {Bucket}/{Key}", bucket, key);

        await _storage.DeleteObjectAsync(bucket, key, ct);
        return Results.NoContent();
    }

    /// <summary>
    /// GET /{bucket}?list-type=2 - List objects V2.
    /// </summary>
    public async Task<IResult> ListObjectsV2Async(
        string bucket,
        string? prefix,
        string? delimiter,
        string? continuationToken,
        string? startAfter,
        int? maxKeys,
        CancellationToken ct)
    {
        _logger.LogDebug(
            "ListObjectsV2: {Bucket}, prefix={Prefix}, delimiter={Delimiter}, maxKeys={MaxKeys}",
            bucket, prefix, delimiter, maxKeys);

        var options = new ListObjectsOptions
        {
            Prefix = prefix,
            Delimiter = delimiter,
            ContinuationToken = continuationToken,
            StartAfter = startAfter,
            MaxKeys = Math.Min(maxKeys ?? 1000, 1000)
        };

        try
        {
            var objects = new List<ObjectInfo>();
            var count = 0;
            string? lastKey = null;

            await foreach (var obj in _storage.ListObjectsAsync(bucket, options, ct))
            {
                if (++count > options.MaxKeys)
                    break;

                objects.Add(obj);
                lastKey = obj.Key;
            }

            var isTruncated = count > options.MaxKeys;
            if (isTruncated)
                objects.RemoveAt(objects.Count - 1);

            // Get common prefixes if delimiter is specified
            List<string>? commonPrefixes = null;
            if (!string.IsNullOrEmpty(delimiter))
            {
                commonPrefixes = new List<string>();
                await foreach (var cp in _storage.ListCommonPrefixesAsync(bucket, prefix, delimiter, ct))
                {
                    commonPrefixes.Add(cp);
                }
            }

            var result = new ListObjectsResult
            {
                Name = bucket,
                Prefix = prefix,
                Delimiter = delimiter,
                MaxKeys = options.MaxKeys,
                IsTruncated = isTruncated,
                ContinuationToken = continuationToken,
                NextContinuationToken = isTruncated ? lastKey : null,
                KeyCount = objects.Count,
                Contents = objects,
                CommonPrefixes = commonPrefixes
            };

            var xml = S3XmlWriter.WriteListBucketResult(result);
            return Results.Content(xml, "application/xml");
        }
        catch (BucketNotFoundException ex)
        {
            return S3Results.Error(ex, 404);
        }
    }

    /// <summary>
    /// POST /{bucket}?delete - Delete multiple objects.
    /// </summary>
    public async Task<IResult> DeleteObjectsAsync(string bucket, HttpRequest request, CancellationToken ct)
    {
        _logger.LogDebug("DeleteObjects: {Bucket}", bucket);

        var (keys, quiet) = S3XmlReader.ParseDeleteObjects(request.Body);

        var deleted = new List<string>();
        var errors = new List<(string Key, string Code, string Message)>();

        foreach (var key in keys)
        {
            try
            {
                var success = await _storage.DeleteObjectAsync(bucket, key, ct);
                if (success || !quiet)
                    deleted.Add(key);
            }
            catch (Exception ex)
            {
                errors.Add((key, "InternalError", ex.Message));
            }
        }

        var xml = S3XmlWriter.WriteDeleteResult(deleted, errors);
        return Results.Content(xml, "application/xml");
    }
}
