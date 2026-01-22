namespace Kurrent.S3;

/// <summary>
/// Configuration options for the S3 server.
/// </summary>
public sealed class S3ServerOptions
{
    /// <summary>
    /// Server region identifier. Used in responses.
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Whether to require authentication for all requests.
    /// </summary>
    public bool RequireAuthentication { get; set; } = false;

    /// <summary>
    /// Credentials for authentication when RequireAuthentication is true.
    /// </summary>
    public List<S3Credential> Credentials { get; set; } = [];

    /// <summary>
    /// Maximum size for a single PUT object request (5GB default).
    /// </summary>
    public long MaxPutObjectSize { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>
    /// Minimum part size for multipart uploads (5MB, last part can be smaller).
    /// </summary>
    public long MinPartSize { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// Maximum number of parts in a multipart upload.
    /// </summary>
    public int MaxPartsPerUpload { get; set; } = 10000;

    /// <summary>
    /// Root directory for file system storage.
    /// </summary>
    public string StoragePath { get; set; } = "./s3-data";

    /// <summary>
    /// Whether to use in-memory storage (for testing).
    /// </summary>
    public bool UseInMemoryStorage { get; set; } = false;
}

/// <summary>
/// S3 credential (access key / secret key pair).
/// </summary>
public sealed class S3Credential
{
    public required string AccessKeyId { get; init; }
    public required string SecretAccessKey { get; init; }
}
