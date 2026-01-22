using DuckDB.NET.Data;

namespace Kurrent.S3.Demo;

/// <summary>
/// Demonstrates DuckDB integration with Kurrent.S3 server.
///
/// Prerequisites:
/// 1. Start the Kurrent.S3 server: cd src/Kurrent.S3 && dotnet run
/// 2. Run this demo: cd src/Kurrent.S3.Demo && dotnet run
/// </summary>
public static class Program
{
    const string S3Endpoint   = "localhost:9000";
    const string S3AccessKey  = "test";
    const string S3SecretKey  = "test";
    const string BucketName   = "demo-bucket";

    public static async Task Main(string[] args)
    {
        Console.WriteLine("=== Kurrent.S3 + DuckDB Demo ===\n");

        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();

        // Step 1: Install and load httpfs extension
        Console.WriteLine("[1] Loading httpfs extension...");
        await ExecuteAsync(connection, "INSTALL httpfs;");
        await ExecuteAsync(connection, "LOAD httpfs;");
        Console.WriteLine("    Done.\n");

        // Step 2: Configure S3 settings to point to our Kurrent.S3 server
        Console.WriteLine("[2] Configuring S3 endpoint...");
        await ExecuteAsync(connection, $"SET s3_endpoint='{S3Endpoint}';");
        await ExecuteAsync(connection, $"SET s3_access_key_id='{S3AccessKey}';");
        await ExecuteAsync(connection, $"SET s3_secret_access_key='{S3SecretKey}';");
        await ExecuteAsync(connection, "SET s3_use_ssl=false;");
        await ExecuteAsync(connection, "SET s3_url_style='path';");
        Console.WriteLine($"    Endpoint: http://{S3Endpoint}");
        Console.WriteLine($"    URL Style: path\n");

        // Step 3: Create a test table with sample data
        Console.WriteLine("[3] Creating test table with sample data...");
        await ExecuteAsync(connection, """
            CREATE TABLE sales (
                id INTEGER,
                product VARCHAR,
                quantity INTEGER,
                price DECIMAL(10,2),
                sale_date DATE
            );
        """);

        await ExecuteAsync(connection, """
            INSERT INTO sales VALUES
                (1, 'Widget A', 10, 29.99, '2024-01-15'),
                (2, 'Widget B', 5, 49.99, '2024-01-16'),
                (3, 'Gadget X', 3, 199.99, '2024-01-17'),
                (4, 'Widget A', 8, 29.99, '2024-01-18'),
                (5, 'Gadget Y', 2, 299.99, '2024-01-19');
        """);
        Console.WriteLine("    Created 'sales' table with 5 rows.\n");

        // Step 4: Write to S3 as Parquet
        Console.WriteLine($"[4] Writing to S3 bucket '{BucketName}'...");
        try
        {
            await ExecuteAsync(connection, $"COPY sales TO 's3://{BucketName}/sales/data.parquet' (FORMAT PARQUET);");
            Console.WriteLine($"    Wrote: s3://{BucketName}/sales/data.parquet\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    ERROR: {ex.Message}");
            Console.WriteLine("    Make sure the Kurrent.S3 server is running!\n");
            return;
        }

        // Step 5: Read back from S3
        Console.WriteLine("[5] Reading back from S3...");
        var count = await ExecuteScalarAsync(connection, $"SELECT COUNT(*) FROM 's3://{BucketName}/sales/data.parquet';");
        Console.WriteLine($"    Row count: {count}\n");

        // Step 6: Query the Parquet file directly from S3
        Console.WriteLine("[6] Querying Parquet file on S3...");
        await using var reader = await ExecuteReaderAsync(connection, $"""
            SELECT product, SUM(quantity) as total_qty, SUM(quantity * price) as revenue
            FROM 's3://{BucketName}/sales/data.parquet'
            GROUP BY product
            ORDER BY revenue DESC;
        """);

        Console.WriteLine("    Product          | Qty | Revenue");
        Console.WriteLine("    -----------------|-----|--------");
        while (await reader.ReadAsync())
        {
            var product  = reader.GetString(0);
            var totalQty = reader.GetInt64(1);
            var revenue  = reader.GetDecimal(2);
            Console.WriteLine($"    {product,-17}| {totalQty,3} | ${revenue:N2}");
        }
        Console.WriteLine();

        // Step 7: Write multiple files for glob demo
        Console.WriteLine("[7] Writing multiple Parquet files for glob pattern demo...");
        await ExecuteAsync(connection, $"""
            COPY (SELECT * FROM sales WHERE sale_date < '2024-01-17')
            TO 's3://{BucketName}/partitioned/sales_batch1.parquet' (FORMAT PARQUET);
        """);
        await ExecuteAsync(connection, $"""
            COPY (SELECT * FROM sales WHERE sale_date >= '2024-01-17')
            TO 's3://{BucketName}/partitioned/sales_batch2.parquet' (FORMAT PARQUET);
        """);
        Console.WriteLine($"    Wrote: s3://{BucketName}/partitioned/sales_batch1.parquet");
        Console.WriteLine($"    Wrote: s3://{BucketName}/partitioned/sales_batch2.parquet\n");

        // Step 8: Use glob pattern to read all files
        Console.WriteLine("[8] Reading all Parquet files using glob pattern...");
        var globCount = await ExecuteScalarAsync(connection, $"SELECT COUNT(*) FROM 's3://{BucketName}/partitioned/*.parquet';");
        Console.WriteLine($"    Glob pattern: s3://{BucketName}/partitioned/*.parquet");
        Console.WriteLine($"    Total rows from all files: {globCount}\n");

        Console.WriteLine("=== Demo Complete ===");
        Console.WriteLine("\nThe Kurrent.S3 server successfully handled:");
        Console.WriteLine("  - PUT object (Parquet writes)");
        Console.WriteLine("  - GET object with Range headers (Parquet reads)");
        Console.WriteLine("  - LIST objects with prefix (glob patterns)");
    }

    static async Task ExecuteAsync(DuckDBConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    static async Task<object?> ExecuteScalarAsync(DuckDBConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    static async Task<DuckDBDataReader> ExecuteReaderAsync(DuckDBConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return (DuckDBDataReader)await cmd.ExecuteReaderAsync();
    }
}
