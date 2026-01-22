using FluentAssertions;
using Kurrent.S3.Storage;
using Kurrent.S3.Storage.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kurrent.S3.Tests.Unit;

public class InMemoryStorageTests
{
    readonly InMemoryStorage _storage = new();

    [Fact]
    public async Task CreateBucket_ShouldSucceed()
    {
        await _storage.CreateBucketAsync("test-bucket");

        var exists = await _storage.BucketExistsAsync("test-bucket");
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task CreateBucket_WhenExists_ShouldThrow()
    {
        await _storage.CreateBucketAsync("duplicate");

        var act = () => _storage.CreateBucketAsync("duplicate");
        await act.Should().ThrowAsync<BucketAlreadyExistsException>();
    }

    [Fact]
    public async Task DeleteBucket_WhenEmpty_ShouldSucceed()
    {
        await _storage.CreateBucketAsync("to-delete");

        await _storage.DeleteBucketAsync("to-delete");

        var exists = await _storage.BucketExistsAsync("to-delete");
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteBucket_WhenNotEmpty_ShouldThrow()
    {
        await _storage.CreateBucketAsync("not-empty");
        await WriteStringAsync("not-empty", "file.txt", "content");

        var act = () => _storage.DeleteBucketAsync("not-empty");
        await act.Should().ThrowAsync<BucketNotEmptyException>();
    }

    [Fact]
    public async Task WriteObject_ShouldComputeCorrectETag()
    {
        await _storage.CreateBucketAsync("etag-test");

        var metadata = await WriteStringAsync("etag-test", "test.txt", "Hello, World!");

        // MD5 of "Hello, World!" in quotes
        metadata.ETag.Should().StartWith("\"");
        metadata.ETag.Should().EndWith("\"");
        metadata.ETag.Length.Should().Be(34); // 32 hex chars + 2 quotes
    }

    [Fact]
    public async Task ReadObject_ShouldReturnContent()
    {
        await _storage.CreateBucketAsync("read-test");
        await WriteStringAsync("read-test", "test.txt", "Test content");

        var content = await ReadStringAsync("read-test", "test.txt");

        content.Should().Be("Test content");
    }

    [Fact]
    public async Task ReadObject_WithRange_ShouldReturnPartialContent()
    {
        await _storage.CreateBucketAsync("range-test");
        await WriteStringAsync("range-test", "test.txt", "0123456789");

        var content = await ReadStringAsync("range-test", "test.txt", rangeStart: 3, rangeEnd: 6);

        content.Should().Be("3456");
    }

    [Fact]
    public async Task ReadObject_NotFound_ShouldThrow()
    {
        await _storage.CreateBucketAsync("not-found-test");

        var act = async () =>
        {
            await foreach (var _ in _storage.ReadObjectAsync("not-found-test", "nonexistent.txt"))
            {
            }
        };

        await act.Should().ThrowAsync<ObjectNotFoundException>();
    }

    [Fact]
    public async Task DeleteObject_ShouldRemoveObject()
    {
        await _storage.CreateBucketAsync("delete-obj-test");
        await WriteStringAsync("delete-obj-test", "test.txt", "content");

        var deleted = await _storage.DeleteObjectAsync("delete-obj-test", "test.txt");

        deleted.Should().BeTrue();

        var metadata = await _storage.GetMetadataAsync("delete-obj-test", "test.txt");
        metadata.Should().BeNull();
    }

    [Fact]
    public async Task ListObjects_ShouldReturnAllObjects()
    {
        await _storage.CreateBucketAsync("list-test");
        await WriteStringAsync("list-test", "file1.txt", "1");
        await WriteStringAsync("list-test", "file2.txt", "2");
        await WriteStringAsync("list-test", "file3.txt", "3");

        var objects = new List<ObjectInfo>();
        await foreach (var obj in _storage.ListObjectsAsync("list-test", new ListObjectsOptions()))
        {
            objects.Add(obj);
        }

        objects.Should().HaveCount(3);
    }

    [Fact]
    public async Task ListObjects_WithPrefix_ShouldFilter()
    {
        await _storage.CreateBucketAsync("prefix-test");
        await WriteStringAsync("prefix-test", "data/file1.txt", "1");
        await WriteStringAsync("prefix-test", "data/file2.txt", "2");
        await WriteStringAsync("prefix-test", "other/file3.txt", "3");

        var objects = new List<ObjectInfo>();
        await foreach (var obj in _storage.ListObjectsAsync("prefix-test",
            new ListObjectsOptions { Prefix = "data/" }))
        {
            objects.Add(obj);
        }

        objects.Should().HaveCount(2);
        objects.Should().AllSatisfy(o => o.Key.Should().StartWith("data/"));
    }

    [Fact]
    public async Task ListObjects_WithDelimiter_ShouldGroupByPrefix()
    {
        await _storage.CreateBucketAsync("delimiter-test");
        await WriteStringAsync("delimiter-test", "folder1/file1.txt", "1");
        await WriteStringAsync("delimiter-test", "folder1/file2.txt", "2");
        await WriteStringAsync("delimiter-test", "folder2/file3.txt", "3");
        await WriteStringAsync("delimiter-test", "root.txt", "4");

        var objects = new List<ObjectInfo>();
        await foreach (var obj in _storage.ListObjectsAsync("delimiter-test",
            new ListObjectsOptions { Delimiter = "/" }))
        {
            objects.Add(obj);
        }

        // Should only return root.txt, folders should be common prefixes
        objects.Should().HaveCount(1);
        objects[0].Key.Should().Be("root.txt");
    }

    [Fact]
    public async Task ListObjects_WithPagination_ShouldWork()
    {
        await _storage.CreateBucketAsync("page-test");
        for (var i = 0; i < 10; i++)
        {
            await WriteStringAsync("page-test", $"file{i:D2}.txt", $"{i}");
        }

        var firstPage = new List<ObjectInfo>();
        string? lastKey = null;
        await foreach (var obj in _storage.ListObjectsAsync("page-test",
            new ListObjectsOptions { MaxKeys = 3 }))
        {
            firstPage.Add(obj);
            lastKey = obj.Key;
        }

        firstPage.Should().HaveCount(3);

        var secondPage = new List<ObjectInfo>();
        await foreach (var obj in _storage.ListObjectsAsync("page-test",
            new ListObjectsOptions { MaxKeys = 3, ContinuationToken = lastKey }))
        {
            secondPage.Add(obj);
        }

        secondPage.Should().HaveCount(3);
        secondPage[0].Key.Should().NotBe(firstPage[0].Key);
    }

    [Fact]
    public async Task ListCommonPrefixes_ShouldReturnFolders()
    {
        await _storage.CreateBucketAsync("common-prefix-test");
        await WriteStringAsync("common-prefix-test", "a/1.txt", "1");
        await WriteStringAsync("common-prefix-test", "a/2.txt", "2");
        await WriteStringAsync("common-prefix-test", "b/3.txt", "3");

        var prefixes = new List<string>();
        await foreach (var prefix in _storage.ListCommonPrefixesAsync("common-prefix-test", null, "/"))
        {
            prefixes.Add(prefix);
        }

        prefixes.Should().BeEquivalentTo(["a/", "b/"]);
    }

    // Multipart upload tests

    [Fact]
    public async Task MultipartUpload_ShouldWork()
    {
        await _storage.CreateBucketAsync("multipart-test");

        // Initiate
        var uploadId = await _storage.InitiateUploadAsync("multipart-test", "large.bin");
        uploadId.Should().NotBeNullOrEmpty();

        // Upload parts
        var part1Data = new byte[1024];
        var part2Data = new byte[1024];
        Random.Shared.NextBytes(part1Data);
        Random.Shared.NextBytes(part2Data);

        var part1 = await UploadPartAsync("multipart-test", "large.bin", uploadId, 1, part1Data);
        var part2 = await UploadPartAsync("multipart-test", "large.bin", uploadId, 2, part2Data);

        // Complete
        var result = await _storage.CompleteUploadAsync("multipart-test", "large.bin", uploadId,
            [new CompletedPart(1, part1.ETag), new CompletedPart(2, part2.ETag)]);

        result.ETag.Should().NotBeNullOrEmpty();

        // Verify content
        var content = await ReadBytesAsync("multipart-test", "large.bin");
        content.Should().HaveCount(2048);
        content[..1024].Should().BeEquivalentTo(part1Data);
        content[1024..].Should().BeEquivalentTo(part2Data);
    }

    [Fact]
    public async Task MultipartUpload_Abort_ShouldCleanup()
    {
        await _storage.CreateBucketAsync("abort-test");

        var uploadId = await _storage.InitiateUploadAsync("abort-test", "aborted.bin");
        await UploadPartAsync("abort-test", "aborted.bin", uploadId, 1, new byte[1024]);

        await _storage.AbortUploadAsync("abort-test", "aborted.bin", uploadId);

        var metadata = await _storage.GetMetadataAsync("abort-test", "aborted.bin");
        metadata.Should().BeNull();
    }

    [Fact]
    public async Task MultipartUpload_ListParts_ShouldReturnUploadedParts()
    {
        await _storage.CreateBucketAsync("list-parts-test");

        var uploadId = await _storage.InitiateUploadAsync("list-parts-test", "parts.bin");
        await UploadPartAsync("list-parts-test", "parts.bin", uploadId, 1, new byte[100]);
        await UploadPartAsync("list-parts-test", "parts.bin", uploadId, 2, new byte[200]);

        var parts = new List<PartInfo>();
        await foreach (var part in _storage.ListPartsAsync("list-parts-test", "parts.bin", uploadId))
        {
            parts.Add(part);
        }

        parts.Should().HaveCount(2);
        parts[0].PartNumber.Should().Be(1);
        parts[0].Size.Should().Be(100);
        parts[1].PartNumber.Should().Be(2);
        parts[1].Size.Should().Be(200);
    }

    // Helper methods

    async Task<ObjectMetadata> WriteStringAsync(string bucket, string key, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return await _storage.WriteObjectAsync(bucket, key, ToAsyncEnumerable(bytes));
    }

    async Task<string> ReadStringAsync(string bucket, string key, long? rangeStart = null, long? rangeEnd = null)
    {
        var bytes = await ReadBytesAsync(bucket, key, rangeStart, rangeEnd);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    async Task<byte[]> ReadBytesAsync(string bucket, string key, long? rangeStart = null, long? rangeEnd = null)
    {
        var chunks = new List<byte[]>();
        await foreach (var chunk in _storage.ReadObjectAsync(bucket, key, rangeStart, rangeEnd))
        {
            chunks.Add(chunk.ToArray());
        }
        return chunks.SelectMany(c => c).ToArray();
    }

    async Task<PartInfo> UploadPartAsync(string bucket, string key, string uploadId, int partNumber, byte[] data)
    {
        return await _storage.UploadPartAsync(bucket, key, uploadId, partNumber, ToAsyncEnumerable(data));
    }

    static async IAsyncEnumerable<ReadOnlyMemory<byte>> ToAsyncEnumerable(byte[] data)
    {
        await Task.Yield();
        yield return data;
    }
}

public class FileSystemStorageTests : IDisposable
{
    readonly string _testDir;
    readonly FileSystemStorage _storage;

    public FileSystemStorageTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"s3-test-{Guid.NewGuid():N}");
        _storage = new FileSystemStorage(_testDir, NullLogger<FileSystemStorage>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task CreateBucket_ShouldCreateDirectory()
    {
        await _storage.CreateBucketAsync("fs-bucket");

        Directory.Exists(Path.Combine(_testDir, "fs-bucket")).Should().BeTrue();
    }

    [Fact]
    public async Task WriteObject_ShouldCreateFile()
    {
        await _storage.CreateBucketAsync("write-fs");

        var data = System.Text.Encoding.UTF8.GetBytes("Hello, FileSystem!");
        await _storage.WriteObjectAsync("write-fs", "test.txt", ToAsyncEnumerable(data));

        var filePath = Path.Combine(_testDir, "write-fs", "test.txt");
        File.Exists(filePath).Should().BeTrue();
        File.ReadAllText(filePath).Should().Be("Hello, FileSystem!");
    }

    [Fact]
    public async Task WriteObject_NestedPath_ShouldCreateDirectories()
    {
        await _storage.CreateBucketAsync("nested-fs");

        var data = System.Text.Encoding.UTF8.GetBytes("Nested content");
        await _storage.WriteObjectAsync("nested-fs", "a/b/c/file.txt", ToAsyncEnumerable(data));

        var filePath = Path.Combine(_testDir, "nested-fs", "a", "b", "c", "file.txt");
        File.Exists(filePath).Should().BeTrue();
    }

    [Fact]
    public async Task ReadObject_WithRange_ShouldSeekCorrectly()
    {
        await _storage.CreateBucketAsync("range-fs");

        var data = System.Text.Encoding.UTF8.GetBytes("ABCDEFGHIJ");
        await _storage.WriteObjectAsync("range-fs", "range.txt", ToAsyncEnumerable(data));

        var chunks = new List<byte[]>();
        await foreach (var chunk in _storage.ReadObjectAsync("range-fs", "range.txt", rangeStart: 2, rangeEnd: 5))
        {
            chunks.Add(chunk.ToArray());
        }

        var result = System.Text.Encoding.UTF8.GetString(chunks.SelectMany(c => c).ToArray());
        result.Should().Be("CDEF");
    }

    static async IAsyncEnumerable<ReadOnlyMemory<byte>> ToAsyncEnumerable(byte[] data)
    {
        await Task.Yield();
        yield return data;
    }
}
