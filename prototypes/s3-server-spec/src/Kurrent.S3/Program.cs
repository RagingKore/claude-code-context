using Kurrent.S3;
using Kurrent.S3.Authentication;
using Kurrent.S3.Handlers;
using Kurrent.S3.Storage;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<S3ServerOptions>(builder.Configuration.GetSection("S3"));

var options = builder.Configuration.GetSection("S3").Get<S3ServerOptions>() ?? new S3ServerOptions();

// Storage
if (options.UseInMemoryStorage)
{
    var storage = new InMemoryStorage();
    builder.Services.AddSingleton<IObjectStorage>(storage);
    builder.Services.AddSingleton<IMultipartUploadManager>(storage);
}
else
{
    builder.Services.AddSingleton<FileSystemStorage>(sp =>
        new FileSystemStorage(
            options.StoragePath,
            sp.GetRequiredService<ILogger<FileSystemStorage>>()));

    builder.Services.AddSingleton<IObjectStorage>(sp => sp.GetRequiredService<FileSystemStorage>());
    builder.Services.AddSingleton<IMultipartUploadManager>(sp => sp.GetRequiredService<FileSystemStorage>());
}

// Authentication
if (options.RequireAuthentication)
{
    builder.Services.AddSingleton<IS3Authenticator, AwsSignatureV4Authenticator>();
}
else
{
    builder.Services.AddSingleton<IS3Authenticator, NoOpAuthenticator>();
}

// Handlers
builder.Services.AddSingleton<BucketHandler>();
builder.Services.AddSingleton<ObjectHandler>();
builder.Services.AddSingleton<MultipartHandler>();

// Logging
builder.Logging.AddConsole();

var app = builder.Build();

// Request logging middleware
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    var start = DateTime.UtcNow;

    logger.LogDebug(
        "Request: {Method} {Path}{Query}",
        context.Request.Method,
        context.Request.Path,
        context.Request.QueryString);

    await next();

    var elapsed = DateTime.UtcNow - start;
    logger.LogInformation(
        "{Method} {Path} => {StatusCode} ({ElapsedMs}ms)",
        context.Request.Method,
        context.Request.Path,
        context.Response.StatusCode,
        elapsed.TotalMilliseconds);
});

// Authentication middleware
app.Use(async (context, next) =>
{
    var authenticator = context.RequestServices.GetRequiredService<IS3Authenticator>();

    try
    {
        await authenticator.AuthenticateAsync(context, context.RequestAborted);
        await next();
    }
    catch (S3AuthenticationException ex)
    {
        context.Response.StatusCode = 403;
        context.Response.ContentType = "application/xml";
        await context.Response.WriteAsync(
            Kurrent.S3.Xml.S3XmlWriter.WriteError("AccessDenied", ex.Message));
    }
});

// Map routes
var bucketHandler = app.Services.GetRequiredService<BucketHandler>();
var objectHandler = app.Services.GetRequiredService<ObjectHandler>();
var multipartHandler = app.Services.GetRequiredService<MultipartHandler>();

// Service-level operations
app.MapGet("/", async (CancellationToken ct) =>
    await bucketHandler.ListBucketsAsync(ct));

// Bucket-level operations
app.MapHead("/{bucket}", async (string bucket, CancellationToken ct) =>
    await bucketHandler.HeadBucketAsync(bucket, ct));

app.MapPut("/{bucket}", async (string bucket, CancellationToken ct) =>
    await bucketHandler.CreateBucketAsync(bucket, ct));

app.MapDelete("/{bucket}", async (string bucket, CancellationToken ct) =>
    await bucketHandler.DeleteBucketAsync(bucket, ct));

// Bucket listing with query parameters
app.MapGet("/{bucket}", async (
    string bucket,
    int? listType,
    string? prefix,
    string? delimiter,
    string? continuationToken,
    string? startAfter,
    int? maxKeys,
    string? uploads,
    CancellationToken ct) =>
{
    // List multipart uploads
    if (uploads != null)
        return await multipartHandler.ListUploadsAsync(bucket, prefix, ct);

    // List objects (V2 if list-type=2, but we handle both the same)
    return await objectHandler.ListObjectsV2Async(
        bucket, prefix, delimiter, continuationToken, startAfter, maxKeys, ct);
});

// Batch delete
app.MapPost("/{bucket}", async (
    string bucket,
    string? delete,
    HttpRequest request,
    CancellationToken ct) =>
{
    if (delete != null)
        return await objectHandler.DeleteObjectsAsync(bucket, request, ct);

    return S3Results.BadRequest("InvalidRequest", "Unknown POST operation");
});

// Object-level operations

// HEAD object
app.MapMethods("/{bucket}/{**key}", ["HEAD"], async (
    string bucket,
    string key,
    HttpContext ctx,
    CancellationToken ct) =>
    await objectHandler.HeadObjectAsync(bucket, key, ctx, ct));

// GET object or list parts
app.MapGet("/{bucket}/{**key}", async (
    string bucket,
    string key,
    string? uploadId,
    HttpContext ctx,
    CancellationToken ct) =>
{
    // List parts if uploadId is present
    if (uploadId != null)
        return await multipartHandler.ListPartsAsync(bucket, key, uploadId, ct);

    // Otherwise stream the object
    await objectHandler.GetObjectAsync(bucket, key, ctx, ct);
    return Results.Empty;
});

// PUT object or upload part
app.MapPut("/{bucket}/{**key}", async (
    string bucket,
    string key,
    int? partNumber,
    string? uploadId,
    HttpRequest request,
    HttpResponse response,
    CancellationToken ct) =>
{
    // Upload part if partNumber and uploadId are present
    if (partNumber.HasValue && uploadId != null)
        return await multipartHandler.UploadPartAsync(
            bucket, key, uploadId, partNumber.Value, request, response, ct);

    // Otherwise put object
    return await objectHandler.PutObjectAsync(bucket, key, request, ct);
});

// POST - initiate multipart upload or complete multipart upload
app.MapPost("/{bucket}/{**key}", async (
    string bucket,
    string key,
    string? uploads,
    string? uploadId,
    HttpRequest request,
    CancellationToken ct) =>
{
    // Initiate multipart upload
    if (uploads != null)
        return await multipartHandler.InitiateUploadAsync(bucket, key, request, ct);

    // Complete multipart upload
    if (uploadId != null)
        return await multipartHandler.CompleteUploadAsync(bucket, key, uploadId, request, ct);

    return S3Results.BadRequest("InvalidRequest", "Unknown POST operation");
});

// DELETE object or abort multipart upload
app.MapDelete("/{bucket}/{**key}", async (
    string bucket,
    string key,
    string? uploadId,
    CancellationToken ct) =>
{
    // Abort multipart upload
    if (uploadId != null)
        return await multipartHandler.AbortUploadAsync(bucket, key, uploadId, ct);

    // Delete object
    return await objectHandler.DeleteObjectAsync(bucket, key, ct);
});

// Run
app.Run();

// Make Program class accessible for testing
public partial class Program { }
