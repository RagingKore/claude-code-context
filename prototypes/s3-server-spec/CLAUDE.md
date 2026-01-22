# Claude Code Instructions

## Project Overview

This is a minimal S3-compatible HTTP server implementation for DuckDB/DuckLake integration. The goal is to provide just enough S3 API compatibility for DuckDB's `httpfs` extension to read and write Parquet files.

## Build & Run

```bash
# Build the solution
dotnet build

# Run the server
cd src/Kurrent.S3
dotnet run

# Run tests
dotnet test
```

## Key Files

- `src/Kurrent.S3/Program.cs` - Main entry point with route definitions
- `src/Kurrent.S3/Storage/IObjectStorage.cs` - Core storage interface
- `src/Kurrent.S3/Storage/FileSystemStorage.cs` - File-based implementation
- `src/Kurrent.S3/Storage/InMemoryStorage.cs` - In-memory implementation for tests
- `src/Kurrent.S3/Handlers/*.cs` - HTTP request handlers
- `src/Kurrent.S3/Xml/*.cs` - S3 XML serialization

## Code Style Preferences

1. **No `Console.WriteLine`** - Use `ILogger` instead
2. **Nullable enabled project-wide** - Don't add `#nullable enable`
3. **Expression-bodied members** - Put `=>` on its own line for multi-line expressions
4. **Align field declarations** - When there are multiple related fields
5. **Inline one-liner methods** - If used only once

## Testing with DuckDB

After starting the server:

```sql
INSTALL httpfs;
LOAD httpfs;

SET s3_endpoint='localhost:9000';
SET s3_access_key_id='test';
SET s3_secret_access_key='test';
SET s3_use_ssl=false;
SET s3_url_style='path';

-- Test write
CREATE TABLE test AS SELECT * FROM range(1000) t(i);
COPY test TO 's3://test-bucket/test.parquet' (FORMAT PARQUET);

-- Test read
SELECT count(*) FROM 's3://test-bucket/test.parquet';

-- Test glob
SELECT count(*) FROM 's3://test-bucket/*.parquet';
```

## Implementation Notes

### IAsyncEnumerable Pattern
All content streaming uses `IAsyncEnumerable<ReadOnlyMemory<byte>>`:
- Avoids buffering entire files
- Enables backpressure
- Memory efficient for large files

### Range Requests
DuckDB reads Parquet files using range requests:
1. First reads footer (last 8 bytes to get metadata offset)
2. Then reads metadata section
3. Then reads specific column chunks

The server must support:
- `Range: bytes=X-Y` (specific range)
- `Range: bytes=X-` (from X to end)
- `Range: bytes=-Y` (last Y bytes)

### Multipart Uploads
DuckDB uses multipart for files > 5MB:
1. `POST /{bucket}/{key}?uploads` - Initiate
2. `PUT /{bucket}/{key}?partNumber=N&uploadId=X` - Upload parts
3. `POST /{bucket}/{key}?uploadId=X` with XML body - Complete

### Authentication
AWS Signature V4 is implemented but disabled by default. Enable in `appsettings.json`:
```json
{
  "S3": {
    "RequireAuthentication": true,
    "Credentials": [...]
  }
}
```

## Common Issues

### DuckDB can't connect
- Check server is running on correct port (default 9000)
- Verify `s3_url_style='path'` is set (not virtual-hosted)
- Check `s3_use_ssl=false` for HTTP

### Range requests failing
- Verify Content-Length header is accurate
- Check Range header parsing handles all formats

### Multipart upload issues
- Minimum part size is 5MB (except last part)
- Parts must be completed in ascending order
- ETags must match exactly

## Future Enhancements

1. **KurrentDB Storage Backend** - Store objects as event streams
2. **Caching Layer** - LRU cache for frequently accessed objects
3. **Compression** - Transparent gzip/zstd for storage
4. **Replication** - Multi-node object storage
