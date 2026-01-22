using Kurrent.S3.Storage;
using Kurrent.S3.Xml;

namespace Kurrent.S3.Handlers;

/// <summary>
/// Helper methods for creating S3-style HTTP results.
/// </summary>
public static class S3Results
{
    public static IResult NotFound(string code, string message, string? resource = null)
    {
        var xml = S3XmlWriter.WriteError(code, message, resource);
        return Results.Content(xml, "application/xml", statusCode: 404);
    }

    public static IResult BadRequest(string code, string message, string? resource = null)
    {
        var xml = S3XmlWriter.WriteError(code, message, resource);
        return Results.Content(xml, "application/xml", statusCode: 400);
    }

    public static IResult Forbidden(string code, string message, string? resource = null)
    {
        var xml = S3XmlWriter.WriteError(code, message, resource);
        return Results.Content(xml, "application/xml", statusCode: 403);
    }

    public static IResult Conflict(string code, string message, string? resource = null)
    {
        var xml = S3XmlWriter.WriteError(code, message, resource);
        return Results.Content(xml, "application/xml", statusCode: 409);
    }

    public static IResult InternalError(string message, string? resource = null)
    {
        var xml = S3XmlWriter.WriteError("InternalError", message, resource);
        return Results.Content(xml, "application/xml", statusCode: 500);
    }

    public static IResult Error(S3StorageException ex, int statusCode)
    {
        var xml = S3XmlWriter.WriteError(ex);
        return Results.Content(xml, "application/xml", statusCode: statusCode);
    }

    public static IResult Error(string code, string message, int statusCode, string? resource = null)
    {
        var xml = S3XmlWriter.WriteError(code, message, resource);
        return Results.Content(xml, "application/xml", statusCode: statusCode);
    }
}
