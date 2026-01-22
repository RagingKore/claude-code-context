using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kurrent.S3.Tests.Integration;

/// <summary>
/// Integration tests using the AWS SDK for .NET.
/// These tests verify compatibility with standard S3 clients.
/// </summary>
public class AwsSdkCompatibilityTests : IClassFixture<S3ServerFixture>, IAsyncLifetime
{
    readonly S3ServerFixture _fixture;
    readonly IAmazonS3 _client;
    readonly string _testBucket;

    public AwsSdkCompatibilityTests(S3ServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
        _testBucket = $"test-bucket-{Guid.NewGuid():N}"[..20];
    }

    public async Task InitializeAsync()
    {
        await _client.PutBucketAsync(_testBucket);
    }

    public async Task DisposeAsync()
    {
        try
        {
            // Delete all objects first
            var objects = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _testBucket
            });

            foreach (var obj in objects.S3Objects)
            {
                await _client.DeleteObjectAsync(_testBucket, obj.Key);
            }

            await _client.DeleteBucketAsync(_testBucket);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task CreateBucket_ShouldSucceed()
    {
        var bucketName = $"new-bucket-{Guid.NewGuid():N}"[..20];

        await _client.PutBucketAsync(bucketName);

        var exists = await AmazonS3Util.DoesS3BucketExistV2Async(_client, bucketName);
        exists.Should().BeTrue();

        // Cleanup
        await _client.DeleteBucketAsync(bucketName);
    }

    [Fact]
    public async Task ListBuckets_ShouldReturnTestBucket()
    {
        var response = await _client.ListBucketsAsync();

        response.Buckets.Should().Contain(b => b.BucketName == _testBucket);
    }

    [Fact]
    public async Task PutObject_ShouldSucceed()
    {
        var key = "test-object.txt";
        var content = "Hello, S3!";

        var response = await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _testBucket,
            Key = key,
            ContentBody = content
        });

        response.ETag.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetObject_ShouldReturnContent()
    {
        var key = "test-get.txt";
        var content = "Test content for GET";

        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _testBucket,
            Key = key,
            ContentBody = content
        });

        var response = await _client.GetObjectAsync(_testBucket, key);
        using var reader = new StreamReader(response.ResponseStream);
        var retrievedContent = await reader.ReadToEndAsync();

        retrievedContent.Should().Be(content);
    }

    [Fact]
    public async Task GetObject_WithRange_ShouldReturnPartialContent()
    {
        var key = "test-range.txt";
        var content = "0123456789ABCDEF";

        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _testBucket,
            Key = key,
            ContentBody = content
        });

        var response = await _client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _testBucket,
            Key = key,
            ByteRange = new ByteRange(5, 10)
        });

        using var reader = new StreamReader(response.ResponseStream);
        var retrievedContent = await reader.ReadToEndAsync();

        retrievedContent.Should().Be("56789A");
    }

    [Fact]
    public async Task HeadObject_ShouldReturnMetadata()
    {
        var key = "test-head.txt";
        var content = "Content for HEAD request";

        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _testBucket,
            Key = key,
            ContentBody = content
        });

        var response = await _client.GetObjectMetadataAsync(_testBucket, key);

        response.ContentLength.Should().Be(content.Length);
        response.ETag.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DeleteObject_ShouldSucceed()
    {
        var key = "test-delete.txt";

        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _testBucket,
            Key = key,
            ContentBody = "To be deleted"
        });

        await _client.DeleteObjectAsync(_testBucket, key);

        var act = () => _client.GetObjectMetadataAsync(_testBucket, key);
        await act.Should().ThrowAsync<AmazonS3Exception>()
            .Where(e => e.StatusCode == System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListObjectsV2_ShouldReturnObjects()
    {
        // Create multiple objects
        for (var i = 0; i < 5; i++)
        {
            await _client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _testBucket,
                Key = $"list-test/object-{i}.txt",
                ContentBody = $"Content {i}"
            });
        }

        var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _testBucket,
            Prefix = "list-test/"
        });

        response.S3Objects.Should().HaveCount(5);
        response.S3Objects.Select(o => o.Key).Should().AllSatisfy(k => k.Should().StartWith("list-test/"));
    }

    [Fact]
    public async Task ListObjectsV2_WithDelimiter_ShouldReturnCommonPrefixes()
    {
        // Create hierarchical structure
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _testBucket,
            Key = "folder1/file1.txt",
            ContentBody = "File 1"
        });
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _testBucket,
            Key = "folder2/file2.txt",
            ContentBody = "File 2"
        });
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _testBucket,
            Key = "root-file.txt",
            ContentBody = "Root file"
        });

        var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _testBucket,
            Delimiter = "/"
        });

        response.CommonPrefixes.Should().Contain("folder1/");
        response.CommonPrefixes.Should().Contain("folder2/");
        response.S3Objects.Should().Contain(o => o.Key == "root-file.txt");
    }

    [Fact]
    public async Task ListObjectsV2_WithPagination_ShouldWork()
    {
        // Create more objects than page size
        for (var i = 0; i < 10; i++)
        {
            await _client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _testBucket,
                Key = $"page-test/object-{i:D3}.txt",
                ContentBody = $"Content {i}"
            });
        }

        // Get first page
        var response1 = await _client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _testBucket,
            Prefix = "page-test/",
            MaxKeys = 3
        });

        response1.S3Objects.Should().HaveCount(3);
        response1.IsTruncated.Should().BeTrue();
        response1.NextContinuationToken.Should().NotBeNullOrEmpty();

        // Get second page
        var response2 = await _client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _testBucket,
            Prefix = "page-test/",
            MaxKeys = 3,
            ContinuationToken = response1.NextContinuationToken
        });

        response2.S3Objects.Should().HaveCount(3);
    }

    [Fact]
    public async Task MultipartUpload_SmallFile_ShouldSucceed()
    {
        var key = "multipart-small.bin";
        var data = new byte[10 * 1024 * 1024]; // 10 MB
        Random.Shared.NextBytes(data);

        // Initiate
        var initResponse = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = _testBucket,
            Key = key
        });

        var uploadId = initResponse.UploadId;
        uploadId.Should().NotBeNullOrEmpty();

        // Upload parts
        var partSize = 5 * 1024 * 1024; // 5 MB
        var parts = new List<PartETag>();

        for (var i = 0; i < 2; i++)
        {
            var partNumber = i + 1;
            var offset = i * partSize;
            var length = Math.Min(partSize, data.Length - offset);

            using var stream = new MemoryStream(data, offset, length);

            var partResponse = await _client.UploadPartAsync(new UploadPartRequest
            {
                BucketName = _testBucket,
                Key = key,
                UploadId = uploadId,
                PartNumber = partNumber,
                InputStream = stream
            });

            parts.Add(new PartETag(partNumber, partResponse.ETag));
        }

        // Complete
        var completeResponse = await _client.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest
            {
                BucketName = _testBucket,
                Key = key,
                UploadId = uploadId,
                PartETags = parts
            });

        completeResponse.ETag.Should().NotBeNullOrEmpty();

        // Verify content
        var getResponse = await _client.GetObjectAsync(_testBucket, key);
        using var resultStream = new MemoryStream();
        await getResponse.ResponseStream.CopyToAsync(resultStream);

        resultStream.ToArray().Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task MultipartUpload_AbortShouldCleanup()
    {
        var key = "multipart-abort.bin";

        // Initiate
        var initResponse = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = _testBucket,
            Key = key
        });

        var uploadId = initResponse.UploadId;

        // Upload one part
        var data = new byte[5 * 1024 * 1024];
        using var stream = new MemoryStream(data);

        await _client.UploadPartAsync(new UploadPartRequest
        {
            BucketName = _testBucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = 1,
            InputStream = stream
        });

        // Abort
        await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
        {
            BucketName = _testBucket,
            Key = key,
            UploadId = uploadId
        });

        // Object should not exist
        var act = () => _client.GetObjectMetadataAsync(_testBucket, key);
        await act.Should().ThrowAsync<AmazonS3Exception>();
    }

    [Fact]
    public async Task DeleteObjects_ShouldDeleteMultiple()
    {
        var keys = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var key = $"batch-delete/object-{i}.txt";
            keys.Add(key);
            await _client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _testBucket,
                Key = key,
                ContentBody = $"Content {i}"
            });
        }

        var response = await _client.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = _testBucket,
            Objects = keys.Select(k => new KeyVersion { Key = k }).ToList()
        });

        response.DeletedObjects.Should().HaveCount(5);

        // Verify all deleted
        var listResponse = await _client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _testBucket,
            Prefix = "batch-delete/"
        });

        listResponse.S3Objects.Should().BeEmpty();
    }
}

/// <summary>
/// Fixture that provides a test S3 server instance.
/// </summary>
public class S3ServerFixture : IAsyncLifetime
{
    WebApplicationFactory<Program>? _factory;
    HttpClient? _httpClient;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("S3:UseInMemoryStorage", "true");
                builder.UseSetting("S3:RequireAuthentication", "false");
            });

        _httpClient = _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _httpClient?.Dispose();
        if (_factory != null)
            await _factory.DisposeAsync();
    }

    public IAmazonS3 CreateClient()
    {
        var config = new AmazonS3Config
        {
            ServiceURL = _httpClient!.BaseAddress!.ToString(),
            ForcePathStyle = true,
            UseHttp = true
        };

        return new AmazonS3Client(new BasicAWSCredentials("test", "test"), config);
    }

    public HttpClient HttpClient => _httpClient!;
}
