# S3-Compatible Server for DuckLake - Implementation Specification

## Overview

Implement a minimal S3-compatible HTTP server in .NET that supports the subset of S3 operations required by DuckDB's `httpfs` extension for DuckLake state storage.

## Target Compatibility

- **DuckDB httpfs extension** - Used by DuckLake for reading/writing Parquet files
- **AWS SDK for .NET** - For integration testing
- **MinIO client** - Alternative client testing

## Required S3 Operations

Based on DuckDB's httpfs documentation, these operations are required:

| Operation | HTTP Method | Path | Purpose |
|-----------|-------------|------|---------|
| HeadObject | HEAD | `/{bucket}/{key}` | Check existence, get metadata |
| GetObject | GET | `/{bucket}/{key}` | Read files (with Range header support) |
| PutObject | PUT | `/{bucket}/{key}` | Write small files |
| DeleteObject | DELETE | `/{bucket}/{key}` | Delete files |
| ListObjectsV2 | GET | `/{bucket}?list-type=2` | Globbing (`*.parquet`) |
| CreateMultipartUpload | POST | `/{bucket}/{key}?uploads` | Initiate large upload |
| UploadPart | PUT | `/{bucket}/{key}?partNumber=N&uploadId=X` | Upload chunk |
| CompleteMultipartUpload | POST | `/{bucket}/{key}?uploadId=X` | Finalize upload |
| AbortMultipartUpload | DELETE | `/{bucket}/{key}?uploadId=X` | Cancel upload |
| HeadBucket | HEAD | `/{bucket}` | Check bucket exists |
| CreateBucket | PUT | `/{bucket}` | Create bucket |

## Project Structure

```
src/
├── Kurrent.S3/
│   ├── Kurrent.S3.csproj
│   ├── S3Server.cs                    # Main server host
│   ├── S3ServerOptions.cs             # Configuration
│   ├── Authentication/
│   │   ├── IS3Authenticator.cs
│   │   ├── AwsSignatureV4Authenticator.cs
│   │   └── NoOpAuthenticator.cs
│   ├── Handlers/
│   │   ├── BucketHandler.cs
│   │   ├── ObjectHandler.cs
│   │   └── MultipartHandler.cs
│   ├── Storage/
│   │   ├── IObjectStorage.cs
│   │   ├── IMultipartUploadManager.cs
│   │   ├── FileSystemStorage.cs
│   │   ├── InMemoryStorage.cs
│   │   └── Models/
│   │       ├── ObjectMetadata.cs
│   │       ├── ObjectInfo.cs
│   │       ├── ListObjectsOptions.cs
│   │       └── PartInfo.cs
│   └── Xml/
│       ├── S3XmlReader.cs
│       └── S3XmlWriter.cs
└── Kurrent.S3.Tests/
    ├── Kurrent.S3.Tests.csproj
    ├── Unit/
    │   ├── FileSystemStorageTests.cs
    │   ├── MultipartUploadTests.cs
    │   └── XmlSerializationTests.cs
    └── Integration/
        ├── AwsSdkCompatibilityTests.cs
        └── DuckDbCompatibilityTests.cs
```

## Design Principles

1. **Streaming everywhere** - Use `IAsyncEnumerable<ReadOnlyMemory<byte>>` to avoid buffering large files
2. **Zero-copy where possible** - Use `ReadOnlyMemory<byte>`, `Span<byte>`, pooled buffers
3. **Incremental hashing** - Compute ETags without buffering entire files
4. **Cancellation support** - All async operations accept `CancellationToken`
5. **Testable** - Interface-based design, in-memory implementation for tests

## Authentication

Support AWS Signature V4 for authenticated requests. For testing, support anonymous access mode.

The signature validation should:
1. Extract `Authorization` header or query string parameters
2. Reconstruct canonical request
3. Validate signature against stored credentials
4. Return 403 if invalid

## Error Handling

Return proper S3 XML error responses:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<Error>
  <Code>NoSuchKey</Code>
  <Message>The specified key does not exist.</Message>
  <Key>example-key</Key>
  <RequestId>request-id</RequestId>
</Error>
```

Standard error codes to implement:
- `NoSuchBucket` (404)
- `NoSuchKey` (404)
- `BucketAlreadyExists` (409)
- `BucketNotEmpty` (409)
- `InvalidArgument` (400)
- `AccessDenied` (403)
- `InternalError` (500)

## Testing Requirements

1. **Unit tests** for storage implementations
2. **Integration tests** using AWS SDK for .NET
3. **DuckDB integration tests** - Actually run DuckDB queries against the server
4. **Multipart upload tests** - Verify large file handling

## Performance Targets

- Support files up to 5GB via multipart upload
- Handle concurrent requests (at least 10 parallel uploads)
- Stream responses without full buffering
- Sub-100ms latency for metadata operations
