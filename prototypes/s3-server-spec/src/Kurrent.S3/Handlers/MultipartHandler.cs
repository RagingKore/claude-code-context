using System.Buffers;
using System.Runtime.CompilerServices;
using Kurrent.S3.Storage;
using Kurrent.S3.Storage.Models;
using Kurrent.S3.Xml;
using Microsoft.Extensions.Logging;

namespace Kurrent.S3.Handlers;

/// <summary>
/// Handles multipart upload S3 operations.
/// </summary>
public sealed class MultipartHandler
{
    readonly IMultipartUploadManager _uploads;
    readonly ILogger<MultipartHandler> _logger;

    public MultipartHandler(IMultipartUploadManager uploads, ILogger<MultipartHandler> logger)
    {
        _uploads = uploads;
        _logger = logger;
    }

    /// <summary>
    /// POST /{bucket}/{key}?uploads - Initiate multipart upload.
    /// </summary>
    public async Task<IResult> InitiateUploadAsync(
        string bucket,
        string key,
        HttpRequest request,
        CancellationToken ct)
    {
        _logger.LogDebug("InitiateMultipartUpload: {Bucket}/{Key}", bucket, key);

        try
        {
            var uploadId = await _uploads.InitiateUploadAsync(
                bucket,
                key,
                request.ContentType,
                ct: ct);

            var result = new InitiateMultipartUploadResult(bucket, key, uploadId);
            var xml = S3XmlWriter.WriteInitiateMultipartUploadResult(result);

            return Results.Content(xml, "application/xml");
        }
        catch (BucketNotFoundException ex)
        {
            return S3Results.Error(ex, 404);
        }
    }

    /// <summary>
    /// PUT /{bucket}/{key}?partNumber=N&uploadId=X - Upload part.
    /// </summary>
    public async Task<IResult> UploadPartAsync(
        string bucket,
        string key,
        string uploadId,
        int partNumber,
        HttpRequest request,
        HttpResponse response,
        CancellationToken ct)
    {
        _logger.LogDebug(
            "UploadPart: {Bucket}/{Key}, uploadId={UploadId}, partNumber={PartNumber}",
            bucket, key, uploadId, partNumber);

        async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadRequestBody(
            [EnumeratorCancellation] CancellationToken ct)
        {
            using var bufferOwner = MemoryPool<byte>.Shared.Rent(81920);
            var buffer = bufferOwner.Memory;

            int bytesRead;
            while ((bytesRead = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                var chunk = new byte[bytesRead];
                buffer[..bytesRead].CopyTo(chunk);
                yield return chunk;
            }
        }

        try
        {
            var partInfo = await _uploads.UploadPartAsync(
                bucket,
                key,
                uploadId,
                partNumber,
                ReadRequestBody(ct),
                ct);

            response.Headers.ETag = partInfo.ETag;
            return Results.Ok();
        }
        catch (NoSuchUploadException ex)
        {
            return S3Results.Error(ex, 404);
        }
        catch (InvalidPartException ex)
        {
            return S3Results.Error(ex, 400);
        }
    }

    /// <summary>
    /// GET /{bucket}/{key}?uploadId=X - List parts.
    /// </summary>
    public async Task<IResult> ListPartsAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken ct)
    {
        _logger.LogDebug("ListParts: {Bucket}/{Key}, uploadId={UploadId}", bucket, key, uploadId);

        try
        {
            var parts = new List<PartInfo>();

            await foreach (var part in _uploads.ListPartsAsync(bucket, key, uploadId, ct))
            {
                parts.Add(part);
            }

            var xml = S3XmlWriter.WriteListPartsResult(bucket, key, uploadId, parts);
            return Results.Content(xml, "application/xml");
        }
        catch (NoSuchUploadException ex)
        {
            return S3Results.Error(ex, 404);
        }
    }

    /// <summary>
    /// POST /{bucket}/{key}?uploadId=X - Complete multipart upload.
    /// </summary>
    public async Task<IResult> CompleteUploadAsync(
        string bucket,
        string key,
        string uploadId,
        HttpRequest request,
        CancellationToken ct)
    {
        _logger.LogDebug("CompleteMultipartUpload: {Bucket}/{Key}, uploadId={UploadId}", bucket, key, uploadId);

        var parts = S3XmlReader.ParseCompleteMultipartUpload(request.Body);

        try
        {
            var result = await _uploads.CompleteUploadAsync(bucket, key, uploadId, parts, ct);
            var xml = S3XmlWriter.WriteCompleteMultipartUploadResult(result);

            return Results.Content(xml, "application/xml");
        }
        catch (NoSuchUploadException ex)
        {
            return S3Results.Error(ex, 404);
        }
        catch (InvalidPartException ex)
        {
            return S3Results.Error(ex, 400);
        }
        catch (InvalidPartOrderException ex)
        {
            return S3Results.Error(ex, 400);
        }
        catch (EntityTooSmallException ex)
        {
            return S3Results.Error(ex, 400);
        }
    }

    /// <summary>
    /// DELETE /{bucket}/{key}?uploadId=X - Abort multipart upload.
    /// </summary>
    public async Task<IResult> AbortUploadAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken ct)
    {
        _logger.LogDebug("AbortMultipartUpload: {Bucket}/{Key}, uploadId={UploadId}", bucket, key, uploadId);

        try
        {
            await _uploads.AbortUploadAsync(bucket, key, uploadId, ct);
            return Results.NoContent();
        }
        catch (NoSuchUploadException ex)
        {
            return S3Results.Error(ex, 404);
        }
    }

    /// <summary>
    /// GET /{bucket}?uploads - List multipart uploads.
    /// </summary>
    public async Task<IResult> ListUploadsAsync(
        string bucket,
        string? prefix,
        CancellationToken ct)
    {
        _logger.LogDebug("ListMultipartUploads: {Bucket}, prefix={Prefix}", bucket, prefix);

        var uploads = new List<MultipartUploadInfo>();

        await foreach (var upload in _uploads.ListUploadsAsync(bucket, prefix, ct))
        {
            uploads.Add(upload);
        }

        var xml = S3XmlWriter.WriteListMultipartUploadsResult(bucket, uploads, prefix);
        return Results.Content(xml, "application/xml");
    }
}
