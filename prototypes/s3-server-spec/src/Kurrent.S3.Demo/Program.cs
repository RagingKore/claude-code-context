using DuckDB.NET.Data;

namespace Kurrent.S3.Demo;

/// <summary>
/// Demonstrates DuckDB and DuckLake integration with Kurrent.S3 server.
///
/// Prerequisites:
/// 1. Start the Kurrent.S3 server: cd src/Kurrent.S3 && dotnet run
/// 2. Run this demo: cd src/Kurrent.S3.Demo && dotnet run [--ducklake]
///
/// Options:
///   --ducklake    Run the DuckLake catalog demo (Part 2) only
///   (no args)     Run the basic httpfs demo (Part 1) only
///   --all         Run both demos
/// </summary>
public static class Program
{
    const string S3Endpoint      = "localhost:9000";
    const string S3AccessKey     = "test";
    const string S3SecretKey     = "test";
    const string BucketName      = "demo-bucket";
    const string DuckLakeBucket  = "ducklake-bucket";

    public static async Task Main(string[] args)
    {
        var runDuckLake = args.Contains("--ducklake") || args.Contains("--all");
        var runBasic    = args.Contains("--all") || !args.Contains("--ducklake");

        using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();

        if (runBasic)
            await RunBasicDemo(connection);

        if (runDuckLake)
            await RunDuckLakeDemo(connection);
    }

    /// <summary>
    /// Part 1: Basic httpfs demo - direct S3 Parquet read/write
    /// </summary>
    static async Task RunBasicDemo(DuckDBConnection connection)
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║        Part 1: Kurrent.S3 + DuckDB httpfs Demo             ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝\n");

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
        await using (var reader = await ExecuteReaderAsync(connection, $"""
            SELECT product, SUM(quantity) as total_qty, SUM(quantity * price) as revenue
            FROM 's3://{BucketName}/sales/data.parquet'
            GROUP BY product
            ORDER BY revenue DESC;
        """))
        {
            Console.WriteLine("    Product          | Qty | Revenue");
            Console.WriteLine("    -----------------|-----|--------");
            while (await reader.ReadAsync())
            {
                var product  = reader.GetString(0);
                var totalQty = reader.GetInt64(1);
                var revenue  = reader.GetDecimal(2);
                Console.WriteLine($"    {product,-17}| {totalQty,3} | ${revenue:N2}");
            }
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

        Console.WriteLine("=== Part 1 Complete ===");
        Console.WriteLine("The Kurrent.S3 server successfully handled:");
        Console.WriteLine("  - PUT object (Parquet writes)");
        Console.WriteLine("  - GET object with Range headers (Parquet reads)");
        Console.WriteLine("  - LIST objects with prefix (glob patterns)\n\n");
    }

    /// <summary>
    /// Part 2: DuckLake catalog demo - managed tables with metadata on S3
    /// </summary>
    static async Task RunDuckLakeDemo(DuckDBConnection connection)
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║        Part 2: Kurrent.S3 + DuckLake Catalog Demo          ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝\n");

        // Step 1: Install required extensions
        Console.WriteLine("[1] Loading extensions (httpfs, ducklake)...");
        await ExecuteAsync(connection, "INSTALL httpfs;");
        await ExecuteAsync(connection, "LOAD httpfs;");
        await ExecuteAsync(connection, "INSTALL ducklake;");
        await ExecuteAsync(connection, "LOAD ducklake;");
        Console.WriteLine("    Done.\n");

        // Step 2: Create S3 secret for Kurrent.S3 server
        Console.WriteLine("[2] Creating S3 secret for Kurrent.S3...");
        await ExecuteAsync(connection, $"""
            CREATE OR REPLACE SECRET kurrent_s3 (
                TYPE s3,
                KEY_ID '{S3AccessKey}',
                SECRET '{S3SecretKey}',
                REGION 'us-east-1',
                ENDPOINT '{S3Endpoint}',
                URL_STYLE 'path',
                USE_SSL false
            );
        """);
        Console.WriteLine($"    Created secret 'kurrent_s3' for http://{S3Endpoint}\n");

        // Step 3: Attach DuckLake catalog backed by S3
        Console.WriteLine("[3] Attaching DuckLake catalog...");
        try
        {
            // DuckLake stores metadata in a local .ducklake file, data in S3
            await ExecuteAsync(connection, $"""
                ATTACH 'ducklake:lake.ducklake' AS lake (
                    DATA_PATH 's3://{DuckLakeBucket}/data/'
                );
            """);
            Console.WriteLine($"    Catalog: lake");
            Console.WriteLine($"    Metadata: lake.ducklake (local)");
            Console.WriteLine($"    Data path: s3://{DuckLakeBucket}/data/\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    ERROR: {ex.Message}");
            Console.WriteLine("    Make sure the Kurrent.S3 server is running!\n");
            return;
        }

        // Step 4: Create schema and tables in DuckLake
        Console.WriteLine("[4] Creating schema and tables in DuckLake catalog...");
        await ExecuteAsync(connection, "CREATE SCHEMA IF NOT EXISTS lake.analytics;");

        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS lake.analytics.events (
                event_id INTEGER,
                event_type VARCHAR,
                user_id INTEGER,
                payload VARCHAR,
                created_at TIMESTAMP
            );
        """);

        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS lake.analytics.users (
                user_id INTEGER,
                username VARCHAR,
                email VARCHAR,
                created_at TIMESTAMP
            );
        """);
        Console.WriteLine("    Created schema: lake.analytics");
        Console.WriteLine("    Created table: lake.analytics.events");
        Console.WriteLine("    Created table: lake.analytics.users\n");

        // Step 5: Insert data into DuckLake tables
        Console.WriteLine("[5] Inserting data into DuckLake tables...");
        await ExecuteAsync(connection, """
            INSERT INTO lake.analytics.users VALUES
                (1, 'alice', 'alice@example.com', '2024-01-01 10:00:00'),
                (2, 'bob', 'bob@example.com', '2024-01-02 11:00:00'),
                (3, 'charlie', 'charlie@example.com', '2024-01-03 12:00:00');
        """);

        await ExecuteAsync(connection, """
            INSERT INTO lake.analytics.events VALUES
                (1, 'login', 1, '{"ip": "192.168.1.1"}', '2024-01-10 09:00:00'),
                (2, 'purchase', 1, '{"item": "widget", "price": 29.99}', '2024-01-10 09:15:00'),
                (3, 'login', 2, '{"ip": "192.168.1.2"}', '2024-01-10 10:00:00'),
                (4, 'logout', 1, '{}', '2024-01-10 09:30:00'),
                (5, 'purchase', 2, '{"item": "gadget", "price": 49.99}', '2024-01-10 10:30:00');
        """);
        Console.WriteLine("    Inserted 3 users and 5 events.\n");

        // Step 6: Query DuckLake tables
        Console.WriteLine("[6] Querying DuckLake tables (data served from S3)...");
        await using (var reader = await ExecuteReaderAsync(connection, """
            SELECT
                u.username,
                COUNT(e.event_id) as event_count,
                COUNT(CASE WHEN e.event_type = 'purchase' THEN 1 END) as purchases
            FROM lake.analytics.users u
            LEFT JOIN lake.analytics.events e ON u.user_id = e.user_id
            GROUP BY u.username
            ORDER BY event_count DESC;
        """))
        {
            Console.WriteLine("    Username   | Events | Purchases");
            Console.WriteLine("    -----------|--------|----------");
            while (await reader.ReadAsync())
            {
                var username   = reader.GetString(0);
                var eventCount = reader.GetInt64(1);
                var purchases  = reader.GetInt64(2);
                Console.WriteLine($"    {username,-10} | {eventCount,6} | {purchases,9}");
            }
        }
        Console.WriteLine();

        // Step 7: Show DuckLake metadata
        Console.WriteLine("[7] Inspecting DuckLake catalog metadata...");
        await using (var reader = await ExecuteReaderAsync(connection, """
            SELECT table_schema, table_name
            FROM information_schema.tables
            WHERE table_catalog = 'lake'
            ORDER BY table_schema, table_name;
        """))
        {
            Console.WriteLine("    Tables in 'lake' catalog:");
            while (await reader.ReadAsync())
            {
                var schema = reader.GetString(0);
                var table  = reader.GetString(1);
                Console.WriteLine($"      - {schema}.{table}");
            }
        }
        Console.WriteLine();

        // Step 8: Demonstrate DuckLake snapshots (time travel)
        Console.WriteLine("[8] DuckLake snapshot info...");
        try
        {
            await using var reader = await ExecuteReaderAsync(connection, """
                SELECT snapshot_id, snapshot_time
                FROM ducklake_snapshots('lake')
                ORDER BY snapshot_time DESC
                LIMIT 5;
            """);

            Console.WriteLine("    Recent snapshots:");
            while (await reader.ReadAsync())
            {
                var snapshotId   = reader.GetValue(0);
                var snapshotTime = reader.GetValue(1);
                Console.WriteLine($"      - Snapshot {snapshotId}: {snapshotTime}");
            }
        }
        catch
        {
            Console.WriteLine("    (Snapshot query not available in this DuckLake version)");
        }
        Console.WriteLine();

        // Step 9: Insert more data to create new snapshot
        Console.WriteLine("[9] Adding more events (creates new snapshot)...");
        await ExecuteAsync(connection, """
            INSERT INTO lake.analytics.events VALUES
                (6, 'login', 3, '{"ip": "192.168.1.3"}', '2024-01-11 08:00:00'),
                (7, 'purchase', 3, '{"item": "super-gadget", "price": 199.99}', '2024-01-11 08:30:00');
        """);
        Console.WriteLine("    Inserted 2 more events for user 'charlie'.\n");

        // Step 10: Verify the data landed on S3
        Console.WriteLine("[10] Verifying data on S3...");
        var totalEvents = await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM lake.analytics.events;");
        var totalUsers  = await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM lake.analytics.users;");
        Console.WriteLine($"    Total events: {totalEvents}");
        Console.WriteLine($"    Total users: {totalUsers}");
        Console.WriteLine($"    Data stored at: s3://{DuckLakeBucket}/data/\n");

        Console.WriteLine("=== Part 2 Complete ===");
        Console.WriteLine("DuckLake catalog features demonstrated:");
        Console.WriteLine("  - Catalog attachment with S3 data path");
        Console.WriteLine("  - Schema and table creation");
        Console.WriteLine("  - ACID inserts with automatic Parquet writes to S3");
        Console.WriteLine("  - Join queries across tables");
        Console.WriteLine("  - Automatic snapshot/versioning on writes");
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
