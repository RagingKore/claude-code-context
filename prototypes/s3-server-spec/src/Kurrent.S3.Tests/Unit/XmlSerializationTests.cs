using System.Text;
using FluentAssertions;
using Kurrent.S3.Storage.Models;
using Kurrent.S3.Xml;

namespace Kurrent.S3.Tests.Unit;

public class XmlSerializationTests
{
    [Fact]
    public void WriteListBucketResult_ShouldProduceValidXml()
    {
        var result = new ListObjectsResult
        {
            Name = "test-bucket",
            Prefix = "data/",
            Delimiter = "/",
            MaxKeys = 1000,
            IsTruncated = false,
            KeyCount = 2,
            Contents =
            [
                new ObjectInfo("data/file1.txt", 100, "\"abc123\"", DateTimeOffset.Parse("2024-01-15T10:30:00Z")),
                new ObjectInfo("data/file2.txt", 200, "\"def456\"", DateTimeOffset.Parse("2024-01-15T11:00:00Z"))
            ],
            CommonPrefixes = ["data/subfolder/"]
        };

        var xml = S3XmlWriter.WriteListBucketResult(result);

        xml.Should().Contain("<Name>test-bucket</Name>");
        xml.Should().Contain("<Prefix>data/</Prefix>");
        xml.Should().Contain("<Delimiter>/</Delimiter>");
        xml.Should().Contain("<MaxKeys>1000</MaxKeys>");
        xml.Should().Contain("<IsTruncated>false</IsTruncated>");
        xml.Should().Contain("<KeyCount>2</KeyCount>");
        xml.Should().Contain("<Key>data/file1.txt</Key>");
        xml.Should().Contain("<Size>100</Size>");
        xml.Should().Contain("<ETag>\"abc123\"</ETag>");
        xml.Should().Contain("<CommonPrefixes><Prefix>data/subfolder/</Prefix></CommonPrefixes>");
    }

    [Fact]
    public void WriteListBucketResult_WithTruncation_ShouldIncludeNextToken()
    {
        var result = new ListObjectsResult
        {
            Name = "bucket",
            MaxKeys = 10,
            IsTruncated = true,
            NextContinuationToken = "last-key",
            KeyCount = 10,
            Contents = []
        };

        var xml = S3XmlWriter.WriteListBucketResult(result);

        xml.Should().Contain("<IsTruncated>true</IsTruncated>");
        xml.Should().Contain("<NextContinuationToken>last-key</NextContinuationToken>");
    }

    [Fact]
    public void WriteInitiateMultipartUploadResult_ShouldProduceValidXml()
    {
        var result = new InitiateMultipartUploadResult("my-bucket", "my-key", "upload-123");

        var xml = S3XmlWriter.WriteInitiateMultipartUploadResult(result);

        xml.Should().Contain("<Bucket>my-bucket</Bucket>");
        xml.Should().Contain("<Key>my-key</Key>");
        xml.Should().Contain("<UploadId>upload-123</UploadId>");
    }

    [Fact]
    public void WriteCompleteMultipartUploadResult_ShouldProduceValidXml()
    {
        var result = new CompleteMultipartUploadResult(
            "/my-bucket/my-key",
            "my-bucket",
            "my-key",
            "\"combined-etag\"");

        var xml = S3XmlWriter.WriteCompleteMultipartUploadResult(result);

        xml.Should().Contain("<Location>/my-bucket/my-key</Location>");
        xml.Should().Contain("<Bucket>my-bucket</Bucket>");
        xml.Should().Contain("<Key>my-key</Key>");
        xml.Should().Contain("<ETag>\"combined-etag\"</ETag>");
    }

    [Fact]
    public void WriteListPartsResult_ShouldProduceValidXml()
    {
        var parts = new List<PartInfo>
        {
            new(1, "\"part1-etag\"", 5242880, DateTimeOffset.Parse("2024-01-15T10:00:00Z")),
            new(2, "\"part2-etag\"", 5242880, DateTimeOffset.Parse("2024-01-15T10:01:00Z"))
        };

        var xml = S3XmlWriter.WriteListPartsResult("bucket", "key", "upload-id", parts);

        xml.Should().Contain("<Bucket>bucket</Bucket>");
        xml.Should().Contain("<Key>key</Key>");
        xml.Should().Contain("<UploadId>upload-id</UploadId>");
        xml.Should().Contain("<PartNumber>1</PartNumber>");
        xml.Should().Contain("<PartNumber>2</PartNumber>");
        xml.Should().Contain("<ETag>\"part1-etag\"</ETag>");
        xml.Should().Contain("<Size>5242880</Size>");
    }

    [Fact]
    public void WriteError_ShouldProduceValidXml()
    {
        var xml = S3XmlWriter.WriteError("NoSuchKey", "The specified key does not exist.", "/bucket/key");

        xml.Should().Contain("<Code>NoSuchKey</Code>");
        xml.Should().Contain("<Message>The specified key does not exist.</Message>");
        xml.Should().Contain("<Resource>/bucket/key</Resource>");
    }

    [Fact]
    public void WriteListAllMyBucketsResult_ShouldProduceValidXml()
    {
        var buckets = new List<(string Name, DateTimeOffset CreationDate)>
        {
            ("bucket-1", DateTimeOffset.Parse("2024-01-01T00:00:00Z")),
            ("bucket-2", DateTimeOffset.Parse("2024-01-02T00:00:00Z"))
        };

        var xml = S3XmlWriter.WriteListAllMyBucketsResult(buckets);

        xml.Should().Contain("<Owner>");
        xml.Should().Contain("<Buckets>");
        xml.Should().Contain("<Bucket><Name>bucket-1</Name>");
        xml.Should().Contain("<Bucket><Name>bucket-2</Name>");
    }

    [Fact]
    public void WriteDeleteResult_ShouldProduceValidXml()
    {
        var deleted = new List<string> { "key1", "key2" };
        var errors = new List<(string Key, string Code, string Message)>
        {
            ("key3", "AccessDenied", "Access denied")
        };

        var xml = S3XmlWriter.WriteDeleteResult(deleted, errors);

        xml.Should().Contain("<Deleted><Key>key1</Key></Deleted>");
        xml.Should().Contain("<Deleted><Key>key2</Key></Deleted>");
        xml.Should().Contain("<Error><Key>key3</Key><Code>AccessDenied</Code>");
    }

    [Fact]
    public void ParseCompleteMultipartUpload_ShouldParseValidXml()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <CompleteMultipartUpload xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                <Part>
                    <PartNumber>1</PartNumber>
                    <ETag>"a54357aff0632cce46d942af68356b38"</ETag>
                </Part>
                <Part>
                    <PartNumber>2</PartNumber>
                    <ETag>"0c78aef83f66abc1fa1e8477f296d394"</ETag>
                </Part>
            </CompleteMultipartUpload>
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var parts = S3XmlReader.ParseCompleteMultipartUpload(stream);

        parts.Should().HaveCount(2);
        parts[0].PartNumber.Should().Be(1);
        parts[0].ETag.Should().Be("\"a54357aff0632cce46d942af68356b38\"");
        parts[1].PartNumber.Should().Be(2);
        parts[1].ETag.Should().Be("\"0c78aef83f66abc1fa1e8477f296d394\"");
    }

    [Fact]
    public void ParseCompleteMultipartUpload_WithoutNamespace_ShouldParse()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <CompleteMultipartUpload>
                <Part>
                    <PartNumber>1</PartNumber>
                    <ETag>"etag1"</ETag>
                </Part>
            </CompleteMultipartUpload>
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var parts = S3XmlReader.ParseCompleteMultipartUpload(stream);

        parts.Should().HaveCount(1);
    }

    [Fact]
    public void ParseDeleteObjects_ShouldParseValidXml()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Delete xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                <Quiet>true</Quiet>
                <Object>
                    <Key>key1</Key>
                </Object>
                <Object>
                    <Key>key2</Key>
                </Object>
            </Delete>
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var (keys, quiet) = S3XmlReader.ParseDeleteObjects(stream);

        keys.Should().BeEquivalentTo(["key1", "key2"]);
        quiet.Should().BeTrue();
    }

    [Fact]
    public void ParseDeleteObjects_WithoutQuiet_ShouldDefaultToFalse()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Delete>
                <Object><Key>key1</Key></Object>
            </Delete>
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var (keys, quiet) = S3XmlReader.ParseDeleteObjects(stream);

        quiet.Should().BeFalse();
    }

    [Fact]
    public void WriteListBucketResult_WithSpecialCharacters_ShouldEscapeXml()
    {
        var result = new ListObjectsResult
        {
            Name = "test-bucket",
            MaxKeys = 1000,
            IsTruncated = false,
            KeyCount = 1,
            Contents =
            [
                new ObjectInfo("file<>&\".txt", 100, "\"abc\"", DateTimeOffset.UtcNow)
            ]
        };

        var xml = S3XmlWriter.WriteListBucketResult(result);

        xml.Should().Contain("file&lt;&gt;&amp;");
        xml.Should().NotContain("<>&");
    }
}
