using System.Xml;
using System.Xml.Linq;
using Kurrent.S3.Storage.Models;

namespace Kurrent.S3.Xml;

/// <summary>
/// XML parsing for S3 requests.
/// </summary>
public static class S3XmlReader
{
    static readonly XNamespace S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    /// <summary>
    /// Parses CompleteMultipartUpload request body.
    /// </summary>
    public static IReadOnlyList<CompletedPart> ParseCompleteMultipartUpload(Stream body)
    {
        var doc = XDocument.Load(body);
        var root = doc.Root;

        if (root == null)
            throw new InvalidOperationException("Invalid XML: missing root element");

        var parts = new List<CompletedPart>();

        // Handle both namespaced and non-namespaced XML
        var partElements = root.Elements(S3Namespace + "Part").ToList();
        if (partElements.Count == 0)
            partElements = root.Elements("Part").ToList();

        foreach (var partElement in partElements)
        {
            var partNumberElement = partElement.Element(S3Namespace + "PartNumber")
                ?? partElement.Element("PartNumber");

            var etagElement = partElement.Element(S3Namespace + "ETag")
                ?? partElement.Element("ETag");

            if (partNumberElement == null || etagElement == null)
                throw new InvalidOperationException("Invalid XML: Part must have PartNumber and ETag");

            if (!int.TryParse(partNumberElement.Value, out var partNumber))
                throw new InvalidOperationException($"Invalid PartNumber: {partNumberElement.Value}");

            parts.Add(new CompletedPart(partNumber, etagElement.Value));
        }

        return parts;
    }

    /// <summary>
    /// Parses Delete request body for batch delete.
    /// </summary>
    public static (IReadOnlyList<string> Keys, bool Quiet) ParseDeleteObjects(Stream body)
    {
        var doc = XDocument.Load(body);
        var root = doc.Root;

        if (root == null)
            throw new InvalidOperationException("Invalid XML: missing root element");

        var quietElement = root.Element(S3Namespace + "Quiet") ?? root.Element("Quiet");
        var quiet = quietElement?.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

        var keys = new List<string>();

        var objectElements = root.Elements(S3Namespace + "Object").ToList();
        if (objectElements.Count == 0)
            objectElements = root.Elements("Object").ToList();

        foreach (var objElement in objectElements)
        {
            var keyElement = objElement.Element(S3Namespace + "Key") ?? objElement.Element("Key");

            if (keyElement == null)
                throw new InvalidOperationException("Invalid XML: Object must have Key");

            keys.Add(keyElement.Value);
        }

        return (keys, quiet);
    }

    /// <summary>
    /// Parses CreateBucketConfiguration for region constraint.
    /// </summary>
    public static string? ParseCreateBucketConfiguration(Stream body)
    {
        if (body.Length == 0)
            return null;

        try
        {
            var doc = XDocument.Load(body);
            var root = doc.Root;

            if (root == null)
                return null;

            var locationElement = root.Element(S3Namespace + "LocationConstraint")
                ?? root.Element("LocationConstraint");

            return locationElement?.Value;
        }
        catch
        {
            return null;
        }
    }
}
