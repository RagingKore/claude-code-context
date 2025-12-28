using GrpcChannel.Client;
using GrpcChannel.Protocol;
using GrpcChannel.Protocol.Contracts;
using Microsoft.Extensions.Logging;

// Setup logging
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Debug);
});

var logger = loggerFactory.CreateLogger<Program>();

Console.WriteLine("=== gRPC Duplex Channel Client Demo ===\n");

// Create client
var options = DuplexClientOptions.ForLocalDevelopment();
var serializer = JsonPayloadSerializer.Default;
var clientLogger = loggerFactory.CreateLogger<DuplexClient>();

await using var client = new DuplexClient(options, serializer, clientLogger);

try
{
    // Connect to server
    Console.WriteLine("Connecting to server...");
    await client.ConnectAsync();
    Console.WriteLine($"Connected!\n");

    // Register client-side handlers (server can call these)
    RegisterClientHandlers(client.Channel, logger);

    // Demo: Client sends requests to server
    await DemoClientToServerRequests(client.Channel, logger);

    // Demo: Notifications
    await DemoNotifications(client.Channel, logger);

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

static void RegisterClientHandlers(IDuplexChannel channel, ILogger logger)
{
    Console.WriteLine("--- Registering Client-Side Handlers ---\n");

    // Handler for server-initiated requests
    channel.OnRequest<ServerPingRequest, ClientPongResponse>("client.ping", async (request, ctx, ct) =>
    {
        logger.LogInformation("Server pinged client: {Message}", request.Message);
        return new ClientPongResponse("pong from client", DateTimeOffset.UtcNow);
    });

    // Handler for server requesting client info
    channel.OnRequest<GetClientInfoRequest, ClientInfoResponse>("client.info", async (request, ctx, ct) =>
    {
        logger.LogInformation("Server requested client info");
        return new ClientInfoResponse(
            Environment.MachineName,
            Environment.OSVersion.ToString(),
            Environment.ProcessId,
            DateTimeOffset.UtcNow);
    });

    // Handler for server-initiated computation
    channel.OnRequest<ComputeRequest, ComputeResponse>("client.compute", async (request, ctx, ct) =>
    {
        logger.LogInformation("Server requested computation: {Expression}", request.Expression);

        // Simple expression evaluation for demo
        var result = request.Expression switch
        {
            "cpu_usage" => Random.Shared.NextDouble() * 100,
            "memory_usage" => GC.GetTotalMemory(false) / 1024.0 / 1024.0,
            _ => 0.0
        };

        return new ComputeResponse(request.Expression, result);
    });

    // Handler for broadcast notifications from server
    channel.OnNotification<BroadcastNotification>("broadcast", async (notification, ctx, ct) =>
    {
        Console.WriteLine($"\n[BROADCAST] {notification.Message}");
    });

    Console.WriteLine("Registered handlers: client.ping, client.info, client.compute, broadcast\n");
}

static async Task DemoClientToServerRequests(IDuplexChannel channel, ILogger logger)
{
    Console.WriteLine("--- Client → Server Requests ---\n");

    // 1. Ping
    Console.WriteLine("1. Ping:");
    var pingResult = await channel.SendRequestAsync<PingRequest, PongResponse>("ping", new PingRequest());
    if (pingResult.IsSuccess)
    {
        Console.WriteLine($"   Pong: {pingResult.Value!.Message} (RTT: {pingResult.DurationMs}ms)");
    }
    else
    {
        Console.WriteLine($"   Failed: {pingResult.Error}");
    }

    // 2. Echo
    Console.WriteLine("\n2. Echo:");
    var echoResult = await channel.SendRequestAsync<EchoRequest, EchoResponse>(
        "echo",
        new EchoRequest("Hello from bidirectional client!"));
    if (echoResult.IsSuccess)
    {
        Console.WriteLine($"   Echo: {echoResult.Value!.Message}");
    }

    // 3. Status
    Console.WriteLine("\n3. Status:");
    var statusResult = await channel.SendRequestAsync<StatusRequest, StatusResponse>("status", new StatusRequest());
    if (statusResult.IsSuccess)
    {
        var status = statusResult.Value!;
        Console.WriteLine($"   Active connections: {status.ActiveConnections}");
        Console.WriteLine($"   Client IDs: [{string.Join(", ", status.ClientIds)}]");
        Console.WriteLine($"   Server uptime: {status.UptimeMs}ms");
    }

    // 4. Math operations
    Console.WriteLine("\n4. Math Operations:");
    var operations = new[]
    {
        ("add", 10.0, 5.0),
        ("subtract", 10.0, 5.0),
        ("multiply", 6.0, 7.0),
        ("divide", 100.0, 4.0)
    };

    foreach (var (op, a, b) in operations)
    {
        var mathResult = await channel.SendRequestAsync<MathRequest, MathResponse>(
            "math",
            new MathRequest(op, a, b));

        if (mathResult.IsSuccess)
        {
            Console.WriteLine($"   {a} {op} {b} = {mathResult.Value!.Result}");
        }
    }

    // 5. Delay (timeout test)
    Console.WriteLine("\n5. Delay Test:");
    Console.WriteLine("   Requesting 500ms delay...");
    var delayResult = await channel.SendRequestAsync<DelayRequest, DelayResponse>(
        "delay",
        new DelayRequest(500));
    if (delayResult.IsSuccess)
    {
        Console.WriteLine($"   Completed in {delayResult.DurationMs}ms");
    }

    // 6. Timeout test
    Console.WriteLine("\n6. Timeout Test:");
    Console.WriteLine("   Requesting 3000ms delay with 1000ms timeout...");
    var timeoutResult = await channel.SendRequestAsync<DelayRequest, DelayResponse>(
        "delay",
        new DelayRequest(3000),
        timeoutMs: 1000);
    Console.WriteLine($"   Status: {timeoutResult.Status} (expected: Timeout)");

    // 7. Method not found
    Console.WriteLine("\n7. Not Found Test:");
    var notFoundResult = await channel.SendRequestAsync<object, object>(
        "nonexistent.method",
        new { });
    Console.WriteLine($"   Status: {notFoundResult.Status} - {notFoundResult.Error}");
}

static async Task DemoNotifications(IDuplexChannel channel, ILogger logger)
{
    Console.WriteLine("\n--- Notifications ---\n");

    // Send notification to server
    Console.WriteLine("Sending notification to server...");
    await channel.SendNotificationAsync(
        "client.event",
        new ClientEventNotification("startup", "Client demo started"));

    Console.WriteLine("Notification sent (fire-and-forget)");
}

// Request/Response DTOs (matching server)
public sealed record PingRequest();
public sealed record PongResponse(string Message, DateTimeOffset Timestamp);

public sealed record EchoRequest(string Message);
public sealed record EchoResponse(string Message, DateTimeOffset Timestamp);

public sealed record StatusRequest();
public sealed record StatusResponse(int ActiveConnections, string[] ClientIds, DateTimeOffset ServerTime, long UptimeMs);

public sealed record MathRequest(string Operation, double A, double B);
public sealed record MathResponse(double Result, string Operation, double A, double B);

public sealed record DelayRequest(int DelayMs);
public sealed record DelayResponse(int DelayedMs, DateTimeOffset CompletedAt);

// Client-side handler DTOs (server can call these)
public sealed record ServerPingRequest(string Message);
public sealed record ClientPongResponse(string Message, DateTimeOffset Timestamp);

public sealed record GetClientInfoRequest();
public sealed record ClientInfoResponse(string MachineName, string OsVersion, int ProcessId, DateTimeOffset Timestamp);

public sealed record ComputeRequest(string Expression);
public sealed record ComputeResponse(string Expression, double Result);

public sealed record BroadcastNotification(string Message);
public sealed record ClientEventNotification(string EventType, string Data);
