using System.Text;
using System.Xml;
using Kurrent.S3.Storage;
using Kurrent.S3.Storage.Models;

namespace Kurrent.S3.Xml;

/// <summary>
/// XML serialization for S3 responses.
/// Uses XmlWriter for efficient streaming output.
/// </summary>
public static class S3XmlWriter
{
    static readonly XmlWriterSettings WriterSettings = new()
    {
        Encoding = Encoding.UTF8,
        Indent = false,
        OmitXmlDeclaration = false,
        Async = true
    };

    const string S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    public static string WriteListBucketResult(ListObjectsResult result)
    {
        using var sw = new StringWriter();
        using var writer = XmlWriter.Create(sw, WriterSettings);

        writer.WriteStartDocument();
        writer.WriteStartElement("ListBucketResult", S3Namespace);

        writer.WriteElementString("Name", result.Name);

        if (result.Prefix != null)
            writer.WriteElementString("Prefix", result.Prefix);
        else
            writer.WriteElementString("Prefix", "");

        if (result.Delimiter != null)
            writer.WriteElementString("Delimiter", result.Delimiter);

        writer.WriteElementString("MaxKeys", result.MaxKeys.ToString());
        writer.WriteElementString("IsTruncated", result.IsTruncated ? "true" : "false");
        writer.WriteElementString("KeyCount", result.KeyCount.ToString());

        if (result.ContinuationToken != null)
            writer.WriteElementString("ContinuationToken", result.ContinuationToken);

        if (result.NextContinuationToken != null)
            writer.WriteElementString("NextContinuationToken", result.NextContinuationToken);

        foreach (var obj in result.Contents)
        {
            writer.WriteStartElement("Contents");
            writer.WriteElementString("Key", obj.Key);
            writer.WriteElementString("LastModified", obj.LastModified.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            writer.WriteElementString("ETag", obj.ETag);
            writer.WriteElementString("Size", obj.Size.ToString());
            writer.WriteElementString("StorageClass", "STANDARD");
            writer.WriteEndElement();
        }

        if (result.CommonPrefixes != null)
        {
            foreach (var prefix in result.CommonPrefixes)
            {
                writer.WriteStartElement("CommonPrefixes");
                writer.WriteElementString("Prefix", prefix);
                writer.WriteEndElement();
            }
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();

        return sw.ToString();
    }

    public static string WriteInitiateMultipartUploadResult(InitiateMultipartUploadResult result)
    {
        using var sw = new StringWriter();
        using var writer = XmlWriter.Create(sw, WriterSettings);

        writer.WriteStartDocument();
        writer.WriteStartElement("InitiateMultipartUploadResult", S3Namespace);
        writer.WriteElementString("Bucket", result.Bucket);
        writer.WriteElementString("Key", result.Key);
        writer.WriteElementString("UploadId", result.UploadId);
        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();

        return sw.ToString();
    }

    public static string WriteCompleteMultipartUploadResult(CompleteMultipartUploadResult result)
    {
        using var sw = new StringWriter();
        using var writer = XmlWriter.Create(sw, WriterSettings);

        writer.WriteStartDocument();
        writer.WriteStartElement("CompleteMultipartUploadResult", S3Namespace);
        writer.WriteElementString("Location", result.Location);
        writer.WriteElementString("Bucket", result.Bucket);
        writer.WriteElementString("Key", result.Key);
        writer.WriteElementString("ETag", result.ETag);
        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();

        return sw.ToString();
    }

    public static string WriteListPartsResult(
        string bucket,
        string key,
        string uploadId,
        IReadOnlyList<PartInfo> parts,
        bool isTruncated = false,
        int? nextPartNumberMarker = null)
    {
        using var sw = new StringWriter();
        using var writer = XmlWriter.Create(sw, WriterSettings);

        writer.WriteStartDocument();
        writer.WriteStartElement("ListPartsResult", S3Namespace);
        writer.WriteElementString("Bucket", bucket);
        writer.WriteElementString("Key", key);
        writer.WriteElementString("UploadId", uploadId);
        writer.WriteElementString("IsTruncated", isTruncated ? "true" : "false");

        if (nextPartNumberMarker.HasValue)
            writer.WriteElementString("NextPartNumberMarker", nextPartNumberMarker.Value.ToString());

        foreach (var part in parts)
        {
            writer.WriteStartElement("Part");
            writer.WriteElementString("PartNumber", part.PartNumber.ToString());
            writer.WriteElementString("LastModified", part.LastModified.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            writer.WriteElementString("ETag", part.ETag);
            writer.WriteElementString("Size", part.Size.ToString());
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();

        return sw.ToString();
    }

    public static string WriteListMultipartUploadsResult(
        string bucket,
        IReadOnlyList<MultipartUploadInfo> uploads,
        string? prefix = null,
        bool isTruncated = false)
    {
        using var sw = new StringWriter();
        using var writer = XmlWriter.Create(sw, WriterSettings);

        writer.WriteStartDocument();
        writer.WriteStartElement("ListMultipartUploadsResult", S3Namespace);
        writer.WriteElementString("Bucket", bucket);

        if (prefix != null)
            writer.WriteElementString("Prefix", prefix);

        writer.WriteElementString("IsTruncated", isTruncated ? "true" : "false");

        foreach (var upload in uploads)
        {
            writer.WriteStartElement("Upload");
            writer.WriteElementString("Key", upload.Key);
            writer.WriteElementString("UploadId", upload.UploadId);
            writer.WriteElementString("Initiated", upload.Initiated.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();

        return sw.ToString();
    }

    public static string WriteListAllMyBucketsResult(IReadOnlyList<(string Name, DateTimeOffset CreationDate)> buckets)
    {
        using var sw = new StringWriter();
        using var writer = XmlWriter.Create(sw, WriterSettings);

        writer.WriteStartDocument();
        writer.WriteStartElement("ListAllMyBucketsResult", S3Namespace);

        writer.WriteStartElement("Owner");
        writer.WriteElementString("ID", "owner-id");
        writer.WriteElementString("DisplayName", "owner");
        writer.WriteEndElement();

        writer.WriteStartElement("Buckets");
        foreach (var (name, creationDate) in buckets)
        {
            writer.WriteStartElement("Bucket");
            writer.WriteElementString("Name", name);
            writer.WriteElementString("CreationDate", creationDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            writer.WriteEndElement();
        }
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();

        return sw.ToString();
    }

    public static string WriteError(string code, string message, string? resource = null, string? requestId = null)
    {
        using var sw = new StringWriter();
        using var writer = XmlWriter.Create(sw, WriterSettings);

        writer.WriteStartDocument();
        writer.WriteStartElement("Error");
        writer.WriteElementString("Code", code);
        writer.WriteElementString("Message", message);

        if (resource != null)
            writer.WriteElementString("Resource", resource);

        if (requestId != null)
            writer.WriteElementString("RequestId", requestId);

        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();

        return sw.ToString();
    }

    public static string WriteError(S3StorageException ex, string? resource = null, string? requestId = null) =>
        WriteError(ex.ErrorCode, ex.Message, resource, requestId);

    public static string WriteCopyObjectResult(string etag, DateTimeOffset lastModified)
    {
        using var sw = new StringWriter();
        using var writer = XmlWriter.Create(sw, WriterSettings);

        writer.WriteStartDocument();
        writer.WriteStartElement("CopyObjectResult", S3Namespace);
        writer.WriteElementString("ETag", etag);
        writer.WriteElementString("LastModified", lastModified.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();

        return sw.ToString();
    }

    public static string WriteDeleteResult(
        IReadOnlyList<string> deleted,
        IReadOnlyList<(string Key, string Code, string Message)> errors)
    {
        using var sw = new StringWriter();
        using var writer = XmlWriter.Create(sw, WriterSettings);

        writer.WriteStartDocument();
        writer.WriteStartElement("DeleteResult", S3Namespace);

        foreach (var key in deleted)
        {
            writer.WriteStartElement("Deleted");
            writer.WriteElementString("Key", key);
            writer.WriteEndElement();
        }

        foreach (var (key, code, message) in errors)
        {
            writer.WriteStartElement("Error");
            writer.WriteElementString("Key", key);
            writer.WriteElementString("Code", code);
            writer.WriteElementString("Message", message);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();

        return sw.ToString();
    }
}
