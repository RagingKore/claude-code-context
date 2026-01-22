using System.Data;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DuckDB.NET.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kurrent.S3.Tests.Integration;

/// <summary>
/// Integration tests using DuckDB's httpfs extension.
/// These tests verify compatibility with DuckLake operations.
/// </summary>
public class DuckDbCompatibilityTests : IClassFixture<S3ServerFixture>, IAsyncLifetime
{
    readonly S3ServerFixture _fixture;
    readonly IAmazonS3 _s3Client;
    readonly string _testBucket;
    DuckDBConnection? _duckdb;

    public DuckDbCompatibilityTests(S3ServerFixture fixture)
    {
        _fixture = fixture;
        _s3Client = fixture.CreateClient();
        _testBucket = $"duckdb-test-{Guid.NewGuid():N}"[..20];
    }

    public async Task InitializeAsync()
    {
        await _s3Client.PutBucketAsync(_testBucket);

        _duckdb = new DuckDBConnection("DataSource=:memory:");
        await _duckdb.OpenAsync();

        // Get the port from the fixture's HTTP client
        var baseUri = _fixture.HttpClient.BaseAddress!;

        // Configure DuckDB for our S3 server
        await ExecuteNonQueryAsync($"""
            INSTALL httpfs;
            LOAD httpfs;
            SET s3_endpoint='{baseUri.Host}:{baseUri.Port}';
            SET s3_access_key_id='test';
            SET s3_secret_access_key='test';
            SET s3_use_ssl=false;
            SET s3_url_style='path';
            """);
    }

    public async Task DisposeAsync()
    {
        if (_duckdb != null)
        {
            await _duckdb.CloseAsync();
            _duckdb.Dispose();
        }

        try
        {
            var objects = await _s3Client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _testBucket
            });

            foreach (var obj in objects.S3Objects)
            {
                await _s3Client.DeleteObjectAsync(_testBucket, obj.Key);
            }

            await _s3Client.DeleteBucketAsync(_testBucket);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task WriteParquet_ShouldSucceed()
    {
        await ExecuteNonQueryAsync($"""
            CREATE TABLE test_data AS 
            SELECT i AS id, 'name_' || i AS name, i * 1.5 AS value
            FROM range(1000) t(i);
            
            COPY test_data TO 's3://{_testBucket}/test.parquet' (FORMAT PARQUET);
            """);

        // Verify file exists via S3
        var metadata = await _s3Client.GetObjectMetadataAsync(_testBucket, "test.parquet");
        metadata.ContentLength.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ReadParquet_ShouldReturnData()
    {
        // First write some data
        await ExecuteNonQueryAsync($"""
            CREATE TABLE source_data AS 
            SELECT i AS id, 'item_' || i AS name
            FROM range(100) t(i);
            
            COPY source_data TO 's3://{_testBucket}/read_test.parquet' (FORMAT PARQUET);
            """);

        // Now read it back
        var count = await ExecuteScalarAsync<long>($"""
            SELECT count(*) FROM 's3://{_testBucket}/read_test.parquet'
            """);

        count.Should().Be(100);
    }

    [Fact]
    public async Task ReadParquet_WithFilter_ShouldPushdownPredicate()
    {
        await ExecuteNonQueryAsync($"""
            CREATE TABLE filter_data AS 
            SELECT i AS id, i % 10 AS category
            FROM range(1000) t(i);
            
            COPY filter_data TO 's3://{_testBucket}/filter_test.parquet' (FORMAT PARQUET);
            """);

        var count = await ExecuteScalarAsync<long>($"""
            SELECT count(*) FROM 's3://{_testBucket}/filter_test.parquet'
            WHERE category = 5
            """);

        count.Should().Be(100); // 1000 / 10 categories
    }

    [Fact]
    public async Task GlobParquet_ShouldMatchMultipleFiles()
    {
        // Write multiple files
        for (var i = 0; i < 3; i++)
        {
            await ExecuteNonQueryAsync($"""
                CREATE OR REPLACE TABLE batch_{i} AS 
                SELECT {i} AS batch, j AS id
                FROM range(100) t(j);
                
                COPY batch_{i} TO 's3://{_testBucket}/data/batch_{i}.parquet' (FORMAT PARQUET);
                """);
        }

        // Read with glob pattern
        var count = await ExecuteScalarAsync<long>($"""
            SELECT count(*) FROM 's3://{_testBucket}/data/*.parquet'
            """);

        count.Should().Be(300); // 3 files × 100 rows
    }

    [Fact]
    public async Task GlobParquet_RecursivePattern_ShouldWork()
    {
        // Create nested directory structure
        await ExecuteNonQueryAsync($"""
            CREATE TABLE nested_1 AS SELECT 1 AS x;
            CREATE TABLE nested_2 AS SELECT 2 AS x;
            
            COPY nested_1 TO 's3://{_testBucket}/nested/a/file1.parquet' (FORMAT PARQUET);
            COPY nested_2 TO 's3://{_testBucket}/nested/b/file2.parquet' (FORMAT PARQUET);
            """);

        // Read with recursive glob
        var count = await ExecuteScalarAsync<long>($"""
            SELECT count(*) FROM 's3://{_testBucket}/nested/**/*.parquet'
            """);

        count.Should().Be(2);
    }

    [Fact]
    public async Task WriteMultipleParquetFiles_WithPartitioning_ShouldWork()
    {
        await ExecuteNonQueryAsync($"""
            CREATE TABLE partitioned AS 
            SELECT i AS id, i % 3 AS category
            FROM range(300) t(i);
            
            COPY partitioned TO 's3://{_testBucket}/partitioned' 
            (FORMAT PARQUET, PARTITION_BY (category));
            """);

        // Verify partitions were created
        var objects = await _s3Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _testBucket,
            Prefix = "partitioned/"
        });

        objects.S3Objects.Should().HaveCountGreaterOrEqualTo(3);
    }

    [Fact]
    public async Task ReadPartitionedData_ShouldWork()
    {
        await ExecuteNonQueryAsync($"""
            CREATE TABLE partitioned_read AS 
            SELECT i AS id, i % 2 AS even_odd
            FROM range(200) t(i);
            
            COPY partitioned_read TO 's3://{_testBucket}/part_read' 
            (FORMAT PARQUET, PARTITION_BY (even_odd));
            """);

        // Read back with partition filter
        var count = await ExecuteScalarAsync<long>($"""
            SELECT count(*) FROM 's3://{_testBucket}/part_read/**/*.parquet'
            WHERE even_odd = 0
            """);

        count.Should().Be(100); // 200 / 2
    }

    [Fact]
    public async Task LargeFile_ShouldTriggerMultipartUpload()
    {
        // Create a larger dataset that will exceed single-part threshold
        // DuckDB's default threshold is 5MB
        await ExecuteNonQueryAsync($"""
            CREATE TABLE large_data AS 
            SELECT i AS id, 
                   repeat('x', 1000) AS padding,
                   random() AS value
            FROM range(10000) t(i);
            
            COPY large_data TO 's3://{_testBucket}/large.parquet' (FORMAT PARQUET);
            """);

        // Verify file was written
        var metadata = await _s3Client.GetObjectMetadataAsync(_testBucket, "large.parquet");

        // Should be larger than 5MB to trigger multipart
        metadata.ContentLength.Should().BeGreaterThan(1024 * 1024);
    }

    [Fact]
    public async Task RangeRead_ParquetFooter_ShouldWork()
    {
        // Parquet files have metadata at the end - DuckDB reads this first
        await ExecuteNonQueryAsync($"""
            CREATE TABLE range_test AS 
            SELECT i AS id FROM range(1000) t(i);
            
            COPY range_test TO 's3://{_testBucket}/range.parquet' (FORMAT PARQUET);
            """);

        // Reading just the schema should work (uses range requests)
        var schema = await ExecuteReaderAsync($"""
            DESCRIBE SELECT * FROM 's3://{_testBucket}/range.parquet'
            """);

        var columns = new List<string>();
        while (await schema.ReadAsync())
        {
            columns.Add(schema.GetString(0));
        }

        columns.Should().Contain("id");
    }

    [Fact]
    public async Task CsvFile_ShouldWork()
    {
        await ExecuteNonQueryAsync($"""
            CREATE TABLE csv_data AS 
            SELECT i AS id, 'name_' || i AS name
            FROM range(100) t(i);
            
            COPY csv_data TO 's3://{_testBucket}/data.csv' (FORMAT CSV, HEADER);
            """);

        var count = await ExecuteScalarAsync<long>($"""
            SELECT count(*) FROM 's3://{_testBucket}/data.csv'
            """);

        count.Should().Be(100);
    }

    [Fact]
    public async Task JsonFile_ShouldWork()
    {
        await ExecuteNonQueryAsync($"""
            CREATE TABLE json_data AS 
            SELECT i AS id, 'value_' || i AS data
            FROM range(50) t(i);
            
            COPY json_data TO 's3://{_testBucket}/data.json' (FORMAT JSON);
            """);

        var count = await ExecuteScalarAsync<long>($"""
            SELECT count(*) FROM read_json_auto('s3://{_testBucket}/data.json')
            """);

        count.Should().Be(50);
    }

    // Helper methods

    async Task ExecuteNonQueryAsync(string sql)
    {
        await using var cmd = _duckdb!.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    async Task<T> ExecuteScalarAsync<T>(string sql)
    {
        await using var cmd = _duckdb!.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T));
    }

    async Task<IDataReader> ExecuteReaderAsync(string sql)
    {
        await using var cmd = _duckdb!.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteReaderAsync();
    }
}
