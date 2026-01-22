using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kurrent.S3.Authentication;

/// <summary>
/// Interface for S3 request authentication.
/// </summary>
public interface IS3Authenticator
{
    /// <summary>
    /// Validates the request authentication.
    /// </summary>
    /// <returns>Access key ID if authenticated, null if anonymous access allowed, throws if unauthorized.</returns>
    ValueTask<string?> AuthenticateAsync(HttpContext context, CancellationToken ct = default);
}

/// <summary>
/// No-op authenticator that allows all requests.
/// </summary>
public sealed class NoOpAuthenticator : IS3Authenticator
{
    public ValueTask<string?> AuthenticateAsync(HttpContext context, CancellationToken ct = default) =>
        ValueTask.FromResult<string?>(null);
}

/// <summary>
/// AWS Signature Version 4 authenticator.
/// </summary>
public sealed partial class AwsSignatureV4Authenticator : IS3Authenticator
{
    readonly IOptions<S3ServerOptions> _options;
    readonly ILogger<AwsSignatureV4Authenticator> _logger;

    public AwsSignatureV4Authenticator(
        IOptions<S3ServerOptions> options,
        ILogger<AwsSignatureV4Authenticator> logger)
    {
        _options = options;
        _logger = logger;
    }

    public ValueTask<string?> AuthenticateAsync(HttpContext context, CancellationToken ct = default)
    {
        var request = context.Request;
        var authHeader = request.Headers.Authorization.FirstOrDefault();

        // Check if authentication is required
        if (!_options.Value.RequireAuthentication)
        {
            // Still try to parse access key if present
            if (authHeader != null && TryParseAccessKey(authHeader, out var accessKey))
                return ValueTask.FromResult<string?>(accessKey);

            return ValueTask.FromResult<string?>(null);
        }

        // Authentication required
        if (string.IsNullOrEmpty(authHeader))
        {
            _logger.LogWarning("Missing Authorization header");
            throw new S3AuthenticationException("Missing Authorization header");
        }

        // Parse AWS4-HMAC-SHA256 signature
        if (!authHeader.StartsWith("AWS4-HMAC-SHA256", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Unsupported authorization type: {Auth}", authHeader[..Math.Min(20, authHeader.Length)]);
            throw new S3AuthenticationException("Unsupported authorization type");
        }

        var parsed = ParseAuthorizationHeader(authHeader);

        if (parsed == null)
        {
            _logger.LogWarning("Failed to parse Authorization header");
            throw new S3AuthenticationException("Invalid Authorization header format");
        }

        var (accessKeyId, signedHeaders, signature, credentialScope) = parsed.Value;

        // Find matching credential
        var credential = _options.Value.Credentials.FirstOrDefault(c =>
            c.AccessKeyId.Equals(accessKeyId, StringComparison.Ordinal));

        if (credential == null)
        {
            _logger.LogWarning("Unknown access key: {AccessKey}", accessKeyId);
            throw new S3AuthenticationException("Invalid access key");
        }

        // Validate signature
        var expectedSignature = CalculateSignature(
            request,
            credential.SecretAccessKey,
            signedHeaders,
            credentialScope);

        if (!string.Equals(signature, expectedSignature, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Signature mismatch for {AccessKey}. Expected: {Expected}, Got: {Got}",
                accessKeyId, expectedSignature, signature);
            throw new S3AuthenticationException("Signature does not match");
        }

        _logger.LogDebug("Authenticated request from {AccessKey}", accessKeyId);
        return ValueTask.FromResult<string?>(accessKeyId);
    }

    static bool TryParseAccessKey(string authHeader, out string accessKey)
    {
        accessKey = "";

        var match = CredentialRegex().Match(authHeader);
        if (!match.Success)
            return false;

        var parts = match.Groups[1].Value.Split('/');
        if (parts.Length == 0)
            return false;

        accessKey = parts[0];
        return true;
    }

    static (string AccessKeyId, string[] SignedHeaders, string Signature, string CredentialScope)?
        ParseAuthorizationHeader(string header)
    {
        // AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request,
        // SignedHeaders=host;range;x-amz-date, Signature=...

        var credentialMatch = CredentialRegex().Match(header);
        var signedHeadersMatch = SignedHeadersRegex().Match(header);
        var signatureMatch = SignatureRegex().Match(header);

        if (!credentialMatch.Success || !signedHeadersMatch.Success || !signatureMatch.Success)
            return null;

        var credential = credentialMatch.Groups[1].Value;
        var signedHeaders = signedHeadersMatch.Groups[1].Value;
        var signature = signatureMatch.Groups[1].Value;

        var credentialParts = credential.Split('/');
        if (credentialParts.Length < 5)
            return null;

        var accessKeyId = credentialParts[0];
        var credentialScope = string.Join("/", credentialParts.Skip(1));

        return (accessKeyId, signedHeaders.Split(';'), signature, credentialScope);
    }

    string CalculateSignature(
        HttpRequest request,
        string secretKey,
        string[] signedHeaders,
        string credentialScope)
    {
        // Build canonical request
        var canonicalRequest = BuildCanonicalRequest(request, signedHeaders);

        // Get date from headers
        var dateHeader = request.Headers["x-amz-date"].FirstOrDefault()
            ?? request.Headers.Date.FirstOrDefault()
            ?? throw new S3AuthenticationException("Missing date header");

        var date = dateHeader.Length >= 8 ? dateHeader[..8] : dateHeader;

        // Build string to sign
        var stringToSign = $"AWS4-HMAC-SHA256\n{dateHeader}\n{credentialScope}\n{Hash(canonicalRequest)}";

        // Calculate signature
        var scopeParts = credentialScope.Split('/');
        var region = scopeParts.Length > 1 ? scopeParts[1] : _options.Value.Region;
        var service = scopeParts.Length > 2 ? scopeParts[2] : "s3";

        var kDate = HmacSha256(Encoding.UTF8.GetBytes($"AWS4{secretKey}"), date);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, service);
        var kSigning = HmacSha256(kService, "aws4_request");

        var signature = HmacSha256(kSigning, stringToSign);
        return Convert.ToHexString(signature).ToLowerInvariant();
    }

    static string BuildCanonicalRequest(HttpRequest request, string[] signedHeaders)
    {
        var method = request.Method;
        var uri = request.Path.Value ?? "/";
        var query = BuildCanonicalQueryString(request.Query);

        var headers = new StringBuilder();
        foreach (var header in signedHeaders.OrderBy(h => h.ToLowerInvariant()))
        {
            var value = header.ToLowerInvariant() switch
            {
                "host" => request.Host.Value,
                _ => request.Headers[header].FirstOrDefault() ?? ""
            };
            headers.AppendLine($"{header.ToLowerInvariant()}:{value.Trim()}");
        }

        var signedHeadersList = string.Join(";", signedHeaders.Select(h => h.ToLowerInvariant()).OrderBy(h => h));

        // For now, use UNSIGNED-PAYLOAD
        var payloadHash = request.Headers["x-amz-content-sha256"].FirstOrDefault() ?? "UNSIGNED-PAYLOAD";

        return $"{method}\n{uri}\n{query}\n{headers}\n{signedHeadersList}\n{payloadHash}";
    }

    static string BuildCanonicalQueryString(IQueryCollection query)
    {
        if (!query.Any())
            return "";

        var sorted = query
            .SelectMany(kvp => kvp.Value.Select(v => (Key: Uri.EscapeDataString(kvp.Key), Value: Uri.EscapeDataString(v ?? ""))))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal);

        return string.Join("&", sorted.Select(p => $"{p.Key}={p.Value}"));
    }

    static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    static byte[] HmacSha256(byte[] key, string value)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    [GeneratedRegex(@"Credential=([^,\s]+)")]
    private static partial Regex CredentialRegex();

    [GeneratedRegex(@"SignedHeaders=([^,\s]+)")]
    private static partial Regex SignedHeadersRegex();

    [GeneratedRegex(@"Signature=([a-fA-F0-9]+)")]
    private static partial Regex SignatureRegex();
}

public class S3AuthenticationException : Exception
{
    public S3AuthenticationException(string message) : base(message) { }
}
