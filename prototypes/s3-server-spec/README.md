# Kurrent.S3 - Minimal S3-Compatible Server for DuckLake

A lightweight S3-compatible HTTP server implementation in .NET 9, designed specifically for DuckDB/DuckLake integration.

## Quick Start

```bash
# Build
cd src/Kurrent.S3
dotnet build

# Run
dotnet run

# Server starts at http://localhost:9000
```

## Configure DuckDB to Use This Server

```sql
INSTALL httpfs;
LOAD httpfs;

SET s3_endpoint='localhost:9000';
SET s3_access_key_id='test';
SET s3_secret_access_key='test';
SET s3_use_ssl=false;
SET s3_url_style='path';

-- Now you can use S3 URLs
COPY my_table TO 's3://my-bucket/data.parquet' (FORMAT PARQUET);
SELECT * FROM 's3://my-bucket/data/*.parquet';
```

## Running Tests

```bash
cd src/Kurrent.S3.Tests
dotnet test
```

## Implemented S3 Operations

| Operation | Method | Path | Status |
|-----------|--------|------|--------|
| ListBuckets | GET | `/` | ✅ |
| HeadBucket | HEAD | `/{bucket}` | ✅ |
| CreateBucket | PUT | `/{bucket}` | ✅ |
| DeleteBucket | DELETE | `/{bucket}` | ✅ |
| ListObjectsV2 | GET | `/{bucket}?list-type=2` | ✅ |
| HeadObject | HEAD | `/{bucket}/{key}` | ✅ |
| GetObject | GET | `/{bucket}/{key}` | ✅ |
| PutObject | PUT | `/{bucket}/{key}` | ✅ |
| DeleteObject | DELETE | `/{bucket}/{key}` | ✅ |
| DeleteObjects | POST | `/{bucket}?delete` | ✅ |
| InitiateMultipartUpload | POST | `/{bucket}/{key}?uploads` | ✅ |
| UploadPart | PUT | `/{bucket}/{key}?partNumber=N&uploadId=X` | ✅ |
| ListParts | GET | `/{bucket}/{key}?uploadId=X` | ✅ |
| CompleteMultipartUpload | POST | `/{bucket}/{key}?uploadId=X` | ✅ |
| AbortMultipartUpload | DELETE | `/{bucket}/{key}?uploadId=X` | ✅ |
| ListMultipartUploads | GET | `/{bucket}?uploads` | ✅ |

## Features

### Range Requests
Full support for HTTP Range headers, essential for DuckDB's Parquet reading:
- DuckDB reads Parquet file footers first (seeks to end)
- Then reads specific column chunks based on query predicates
- Server supports `bytes=start-end`, `bytes=start-`, and `bytes=-suffix` formats

### Streaming
All operations use `IAsyncEnumerable<ReadOnlyMemory<byte>>` for memory-efficient streaming:
- No buffering of entire files in memory
- Incremental MD5 computation for ETags
- Backpressure-aware response streaming

### Multipart Uploads
Required for files > 5MB (DuckDB's default threshold):
- Parts stored in temp directory during upload
- Concatenated on completion
- Automatic cleanup on abort

### Prefix/Delimiter Listing
Full support for S3's virtual directory structure:
- Prefix filtering for glob patterns (`data/*.parquet`)
- Delimiter support for folder-like navigation
- Pagination with continuation tokens

## Configuration

Edit `appsettings.json`:

```json
{
  "S3": {
    "Region": "us-east-1",
    "RequireAuthentication": false,
    "StoragePath": "./s3-data",
    "UseInMemoryStorage": false,
    "Credentials": [
      {
        "AccessKeyId": "your-access-key",
        "SecretAccessKey": "your-secret-key"
      }
    ]
  }
}
```

### Options

| Option | Default | Description |
|--------|---------|-------------|
| `Region` | `us-east-1` | Region identifier in responses |
| `RequireAuthentication` | `false` | Enable AWS Signature V4 validation |
| `StoragePath` | `./s3-data` | Root directory for file storage |
| `UseInMemoryStorage` | `false` | Use in-memory storage (for testing) |
| `MaxPutObjectSize` | 5GB | Maximum single PUT request size |
| `MinPartSize` | 5MB | Minimum multipart part size |
| `MaxPartsPerUpload` | 10000 | Maximum parts per multipart upload |

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     ASP.NET Core Minimal API                     │
│  Routes: /{bucket}, /{bucket}/{key}, query params               │
└─────────────────────────────┬───────────────────────────────────┘
                              │
┌─────────────────────────────▼───────────────────────────────────┐
│                         Handlers                                 │
│  BucketHandler, ObjectHandler, MultipartHandler                 │
└─────────────────────────────┬───────────────────────────────────┘
                              │
┌─────────────────────────────▼───────────────────────────────────┐
│                    Storage Interfaces                            │
│  IObjectStorage, IMultipartUploadManager                        │
└─────────────────────────────┬───────────────────────────────────┘
                              │
        ┌─────────────────────┼─────────────────────┐
        ▼                     ▼                     ▼
┌───────────────┐   ┌─────────────────┐   ┌─────────────────┐
│ FileSystem    │   │ InMemory        │   │ (Future:        │
│ Storage       │   │ Storage         │   │  KurrentDB)     │
└───────────────┘   └─────────────────┘   └─────────────────┘
```

## Key Design Decisions

### 1. IAsyncEnumerable Throughout
All content is streamed as `IAsyncEnumerable<ReadOnlyMemory<byte>>`:

```csharp
public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadObjectAsync(
    string bucket,
    string key,
    long? rangeStart = null,
    long? rangeEnd = null,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    // Stream directly from disk, no full buffering
}
```

### 2. Incremental Hashing
ETags computed incrementally during write:

```csharp
using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

await foreach (var chunk in content.WithCancellation(ct))
{
    md5.AppendData(chunk.Span);
    await stream.WriteAsync(chunk, ct);
}

var etag = $"\"{Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant()}\"";
```

### 3. Memory Pooling
Buffers are rented from `MemoryPool<byte>.Shared`:

```csharp
using var bufferOwner = MemoryPool<byte>.Shared.Rent(81920);
var buffer = bufferOwner.Memory;
```

### 4. Atomic Writes
Objects written to temp file, then moved atomically:

```csharp
var tempPath = path + $".tmp.{Guid.NewGuid():N}";
// ... write to tempPath ...
File.Move(tempPath, path, overwrite: true);
```

## Testing with AWS SDK

```csharp
var config = new AmazonS3Config
{
    ServiceURL = "http://localhost:9000",
    ForcePathStyle = true,
    UseHttp = true
};

var client = new AmazonS3Client(
    new BasicAWSCredentials("test", "test"),
    config);

await client.PutObjectAsync(new PutObjectRequest
{
    BucketName = "my-bucket",
    Key = "test.txt",
    ContentBody = "Hello, S3!"
});
```

## Known Limitations

1. **No versioning** - Objects are overwritten, no version history
2. **No ACLs** - All objects are private by default
3. **No lifecycle policies** - Objects persist until explicitly deleted
4. **No server-side encryption** - Data stored in plaintext
5. **No cross-region replication** - Single-node only
6. **Limited metadata** - Only ContentType and user metadata supported

## Extending for KurrentDB Storage

To store objects as events in KurrentDB, implement `IObjectStorage`:

```csharp
public sealed class KurrentObjectStorage : IObjectStorage
{
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadObjectAsync(
        string bucket,
        string key,
        long? rangeStart = null,
        long? rangeEnd = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var streamName = $"s3-{bucket}-{ComputeKeyHash(key)}";
        
        await foreach (var @event in _client.ReadStreamAsync(streamName, ...))
        {
            if (@event.Event.EventType == "ObjectChunk")
                yield return @event.Event.Data;
        }
    }
}
```

## Performance Considerations

1. **Large files**: Use multipart uploads for files > 100MB
2. **Many small files**: Consider batching operations
3. **High concurrency**: FileSystemStorage uses file locks
4. **Memory**: 80KB buffer size balances throughput vs memory

## Docker Support

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 9000

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish "src/Kurrent.S3/Kurrent.S3.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
VOLUME /app/s3-data
ENTRYPOINT ["dotnet", "Kurrent.S3.dll"]
```

```bash
docker build -t kurrent-s3 .
docker run -p 9000:9000 -v $(pwd)/data:/app/s3-data kurrent-s3
```

## License

MIT
