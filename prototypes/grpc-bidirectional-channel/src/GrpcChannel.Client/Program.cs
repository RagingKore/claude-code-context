using GrpcChannel.Client;
using GrpcChannel.Protocol.Messages;
using Microsoft.Extensions.Logging;

// Setup logging
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Debug);
});

var logger = loggerFactory.CreateLogger<Program>();

Console.WriteLine("=== gRPC Protocol Client Demo ===\n");

// Create unified protocol client (handles both RPC and data streams)
var options = ProtocolClientOptions.ForLocalDevelopment();
var clientLogger = loggerFactory.CreateLogger<ProtocolClient>();

await using var client = new ProtocolClient(options, logger: clientLogger);

try
{
    // Connect to server
    Console.WriteLine("Connecting to server...");
    await client.ConnectAsync();
    Console.WriteLine($"Connected!\n");

    // Register client-side handlers (server can call these)
    RegisterClientHandlers(client, logger);

    // Demo: Protobuf message requests
    await DemoProtobufRequests(client, logger);

    // Demo: C# record requests (JSON serialized)
    await DemoRecordRequests(client, logger);

    // Demo: Notifications
    await DemoNotifications(client, logger);

    // Demo: High-throughput data streams (uses same client)
    await DemoDataStreams(client, logger);

    // Keep running to receive server requests
    Console.WriteLine("\n--- Listening for server requests (press Enter to exit) ---");
    Console.ReadLine();
}
catch (Exception ex)
{
    logger.LogError(ex, "Demo failed");
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine("Make sure the server is running on https://localhost:5001");
}

static void RegisterClientHandlers(ProtocolClient client, ILogger logger)
{
    Console.WriteLine("--- Registering Client-Side Handlers ---\n");

    // Handler for server-initiated ping requests (protobuf)
    client.OnRequest<ServerPingRequest, ClientPongResponse>("client.ping", async (request, ctx, ct) =>
    {
        logger.LogInformation("[Proto] Server pinged client: {Message}", request.Message);
        return new ClientPongResponse
        {
            Message = "pong from client",
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    });

    // Handler for server requesting client info (protobuf)
    client.OnRequest<GetClientInfoRequest, ClientInfoResponse>("client.info", async (request, ctx, ct) =>
    {
        logger.LogInformation("[Proto] Server requested client info");
        return new ClientInfoResponse
        {
            MachineName = Environment.MachineName,
            OsVersion = Environment.OSVersion.ToString(),
            ProcessId = Environment.ProcessId,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    });

    // Handler for server-initiated computation (protobuf)
    client.OnRequest<ComputeRequest, ComputeResponse>("client.compute", async (request, ctx, ct) =>
    {
        logger.LogInformation("[Proto] Server requested computation: {Expression}", request.Expression);

        // Simple expression evaluation for demo
        var result = request.Expression switch
        {
            "cpu_usage" => Random.Shared.NextDouble() * 100,
            "memory_usage" => GC.GetTotalMemory(false) / 1024.0 / 1024.0,
            _ => 0.0
        };

        return new ComputeResponse
        {
            Expression = request.Expression,
            Result = result
        };
    });

    // Handler for C# record requests from server (JSON)
    client.OnRequest<ClientHealthCheckRequest, ClientHealthCheckResponse>("client.health", async (request, ctx, ct) =>
    {
        logger.LogInformation("[JSON] Server requested health check");
        return new ClientHealthCheckResponse(
            Status: "healthy",
            Uptime: TimeSpan.FromMilliseconds(Environment.TickCount64),
            MemoryUsageMb: GC.GetTotalMemory(false) / 1024.0 / 1024.0,
            Timestamp: DateTimeOffset.UtcNow);
    });

    // Handler for broadcast notifications from server (protobuf)
    client.OnNotification<BroadcastNotification>("broadcast", async (notification, ctx, ct) =>
    {
        Console.WriteLine($"\n[Proto BROADCAST] {notification.Message}");
    });

    // Handler for custom record notifications (JSON)
    client.OnNotification<ServerNotificationRecord>("server.notification", async (notification, ctx, ct) =>
    {
        Console.WriteLine($"\n[JSON NOTIFICATION] {notification.Type}: {notification.Message}");
    });

    Console.WriteLine("Registered protobuf handlers: client.ping, client.info, client.compute, broadcast");
    Console.WriteLine("Registered JSON handlers: client.health, server.notification\n");
}

static async Task DemoProtobufRequests(ProtocolClient client, ILogger logger)
{
    Console.WriteLine("=== PROTOBUF MESSAGE REQUESTS ===\n");

    // 1. Ping
    Console.WriteLine("1. Ping (protobuf):");
    var pingResult = await client.SendRequestAsync<PingRequest, PongResponse>("ping", new PingRequest());
    if (pingResult.IsSuccess)
    {
        Console.WriteLine($"   Pong: {pingResult.Value!.Message} (RTT: {pingResult.DurationMs}ms)");
    }
    else
    {
        Console.WriteLine($"   Failed: {pingResult.Problem?.Detail}");
    }

    // 2. Echo
    Console.WriteLine("\n2. Echo (protobuf):");
    var echoResult = await client.SendRequestAsync<EchoRequest, EchoResponse>(
        "echo",
        new EchoRequest { Message = "Hello from bidirectional client!" });
    if (echoResult.IsSuccess)
    {
        Console.WriteLine($"   Echo: {echoResult.Value!.Message}");
    }

    // 3. Status
    Console.WriteLine("\n3. Status (protobuf):");
    var statusResult = await client.SendRequestAsync<StatusRequest, StatusResponse>("status", new StatusRequest());
    if (statusResult.IsSuccess)
    {
        var status = statusResult.Value!;
        Console.WriteLine($"   Active connections: {status.ActiveConnections}");
        Console.WriteLine($"   Client IDs: [{string.Join(", ", status.ClientIds)}]");
        Console.WriteLine($"   Server uptime: {status.UptimeMs}ms");
    }

    // 4. Math operations
    Console.WriteLine("\n4. Math Operations (protobuf):");
    var operations = new[]
    {
        ("add", 10.0, 5.0),
        ("multiply", 6.0, 7.0)
    };

    foreach (var (op, a, b) in operations)
    {
        var mathResult = await client.SendRequestAsync<MathRequest, MathResponse>(
            "math",
            new MathRequest { Operation = op, A = a, B = b });

        if (mathResult.IsSuccess)
        {
            Console.WriteLine($"   {a} {op} {b} = {mathResult.Value!.Result}");
        }
    }
}

static async Task DemoRecordRequests(ProtocolClient client, ILogger logger)
{
    Console.WriteLine("\n=== C# RECORD REQUESTS (JSON serialized) ===\n");

    // 1. Greeting (simple record)
    Console.WriteLine("1. Greeting (C# record):");
    var greetResult = await client.SendRequestAsync<GreetingRequest, GreetingResponse>(
        "greet",
        new GreetingRequest("World", "spanish"));
    if (greetResult.IsSuccess)
    {
        Console.WriteLine($"   Response: {greetResult.Value!.Greeting}");
        Console.WriteLine($"   Timestamp: {greetResult.Value.Timestamp}");
    }
    else
    {
        Console.WriteLine($"   Failed: {greetResult.Problem?.Detail}");
    }

    // 2. Process data (complex nested records)
    Console.WriteLine("\n2. Process Data (nested records):");
    var processResult = await client.SendRequestAsync<ProcessDataRequest, ProcessDataResponse>(
        "process",
        new ProcessDataRequest(["hello", "world", "duplex"]));
    if (processResult.IsSuccess)
    {
        var response = processResult.Value!;
        Console.WriteLine($"   Processed {response.TotalProcessed} items:");
        foreach (var item in response.Results)
        {
            Console.WriteLine($"     [{item.Id}] {item.OriginalValue} -> {item.ProcessedValue} (length: {item.Length})");
        }
    }

    // 3. Get user (record with collections and dictionary)
    Console.WriteLine("\n3. Get User (record with collections):");
    var userResult = await client.SendRequestAsync<GetUserRequest, UserInfo>(
        "user.get",
        new GetUserRequest(42));
    if (userResult.IsSuccess)
    {
        var user = userResult.Value!;
        Console.WriteLine($"   ID: {user.Id}");
        Console.WriteLine($"   Name: {user.Name}");
        Console.WriteLine($"   Email: {user.Email}");
        Console.WriteLine($"   Roles: [{string.Join(", ", user.Roles)}]");
        Console.WriteLine($"   Metadata: {string.Join(", ", user.Metadata.Select(kv => $"{kv.Key}={kv.Value}"))}");
    }

    // 4. Timeout test
    Console.WriteLine("\n4. Timeout Test (protobuf):");
    Console.WriteLine("   Requesting 3000ms delay with 1000ms timeout...");
    var timeoutResult = await client.SendRequestAsync<DelayRequest, DelayResponse>(
        "delay",
        new DelayRequest { DelayMs = 3000 },
        timeoutMs: 1000);
    Console.WriteLine($"   Status: {timeoutResult.Status} (expected: Timeout)");

    // 5. Method not found test
    Console.WriteLine("\n5. Not Found Test:");
    var notFoundResult = await client.SendRequestAsync<GreetingRequest, GreetingResponse>(
        "nonexistent.method",
        new GreetingRequest("test"));
    Console.WriteLine($"   Status: {notFoundResult.Status} - {notFoundResult.Problem?.Detail}");
}

static async Task DemoNotifications(ProtocolClient client, ILogger logger)
{
    Console.WriteLine("\n=== NOTIFICATIONS ===\n");

    // Send protobuf notification
    Console.WriteLine("Sending protobuf notification...");
    await client.SendNotificationAsync(
        "client.event",
        new ClientEventNotification
        {
            EventType = "startup",
            Data = "Client demo started",
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    Console.WriteLine("Protobuf notification sent");

    // Send C# record notification
    Console.WriteLine("Sending C# record notification...");
    await client.SendNotificationAsync(
        "custom.event",
        new CustomEventRecord("demo_event", new { Source = "client", Action = "test" }, DateTimeOffset.UtcNow));
    Console.WriteLine("JSON notification sent");
}

static async Task DemoDataStreams(ProtocolClient client, ILogger logger)
{
    Console.WriteLine("\n=== HIGH-THROUGHPUT DATA STREAMS ===\n");

    // 1. Batch stream - finite stream that completes
    Console.WriteLine("1. Batch Stream (finite, 10 items):");
    var batchOptions = new Dictionary<string, string> { ["batch_size"] = "10" };
    var batchCount = 0;

    await foreach (var item in client.SubscribeAsync<BatchItem>(
        "batch",
        streamOptions: batchOptions))
    {
        batchCount++;
        Console.WriteLine($"   [{item.Sequence}] {item.Payload?.Name} = {item.Payload?.Value}");
    }
    Console.WriteLine($"   Batch complete: {batchCount} items received\n");

    // 2. Counter stream - sample a few messages then cancel
    Console.WriteLine("2. Counter Stream (sampling 5 messages at 10/sec):");
    using var counterCts = new CancellationTokenSource();
    var counterCount = 0;

    try
    {
        await foreach (var item in client.SubscribeAsync<CounterEvent>(
            "counter",
            maxRate: 10,
            cancellationToken: counterCts.Token))
        {
            counterCount++;
            Console.WriteLine($"   [{item.Sequence}] Count: {item.Payload?.Count} at {item.Payload?.Timestamp:HH:mm:ss.fff}");

            if (counterCount >= 5)
            {
                counterCts.Cancel();
            }
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine($"   Stream cancelled after {counterCount} messages\n");
    }

    // 3. Events stream with filter
    Console.WriteLine("3. Events Stream (filtered to 'user.*', sampling 5 messages):");
    using var eventsCts = new CancellationTokenSource();
    var eventsCount = 0;

    try
    {
        await foreach (var item in client.SubscribeAsync<StreamEvent>(
            "events",
            filter: "user",
            cancellationToken: eventsCts.Token))
        {
            eventsCount++;
            Console.WriteLine($"   [{item.Sequence}] {item.Payload?.EventType}: {item.Payload?.Data}");

            if (eventsCount >= 5)
            {
                eventsCts.Cancel();
            }
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine($"   Stream cancelled after {eventsCount} messages\n");
    }

    Console.WriteLine("Data stream demos complete!");
}

// =============================================
// C# RECORD TYPES (serialized with JSON via RawPayload)
// These must match the server-side definitions
// =============================================

// Greeting request/response (from server)
public sealed record GreetingRequest(string Name, string? Language = null);
public sealed record GreetingResponse(string Greeting, DateTimeOffset Timestamp);

// Complex data processing (from server)
public sealed record ProcessDataRequest(List<string> Items);
public sealed record ProcessDataResponse(List<ProcessedItem> Results, int TotalProcessed, DateTimeOffset ProcessedAt);
public sealed record ProcessedItem(int Id, string OriginalValue, string ProcessedValue, int Length);

// User info (from server)
public sealed record GetUserRequest(int UserId);
public sealed record UserInfo(
    int Id,
    string Name,
    string Email,
    List<string> Roles,
    Dictionary<string, string> Metadata,
    DateTimeOffset CreatedAt);

// Custom event notification (bidirectional)
public sealed record CustomEventRecord(string EventType, object? Payload, DateTimeOffset Timestamp);

// Client health check (server -> client via JSON)
public sealed record ClientHealthCheckRequest();
public sealed record ClientHealthCheckResponse(
    string Status,
    TimeSpan Uptime,
    double MemoryUsageMb,
    DateTimeOffset Timestamp);

// Server notification to client (JSON)
public sealed record ServerNotificationRecord(string Type, string Message, DateTimeOffset Timestamp);

// =============================================
// DATA STREAM EVENT TYPES (must match server)
// =============================================

// Counter stream event
public sealed record CounterEvent(long Count, DateTimeOffset Timestamp);

// Generic stream event
public sealed record StreamEvent(long Sequence, string EventType, string Data, DateTimeOffset Timestamp);

// Batch item
public sealed record BatchItem(int Id, string Name, double Value);
