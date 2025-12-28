using GrpcChannel.Client;
using GrpcChannel.Protocol.Contracts;
using GrpcChannel.Protocol.Messages;
using Microsoft.Extensions.Logging;

// Setup logging
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Debug);
});

var logger = loggerFactory.CreateLogger<Program>();

Console.WriteLine("=== gRPC Bidirectional Channel Client Demo ===\n");

// Create connection options
var options = new ChannelConnectionOptions(
    ServerAddress: "https://localhost:5001",
    ClientId: $"demo-client-{Environment.ProcessId}");

var factory = new GrpcChannelClientFactory(loggerFactory);

// Demo 1: Message Channel
await DemoMessageChannelAsync(factory, options, logger);

// Demo 2: Command Channel
await DemoCommandChannelAsync(factory, options, logger);

Console.WriteLine("\n=== Demo Complete ===");

static async Task DemoMessageChannelAsync(
    IChannelClientFactory factory,
    ChannelConnectionOptions options,
    ILogger logger)
{
    Console.WriteLine("\n--- Message Channel Demo ---\n");

    try
    {
        await using var connection = await factory.CreateConnectionAsync(options);

        Console.WriteLine($"Connected with ID: {connection.ConnectionId}");

        // Setup message handler
        connection.MessageReceived += (_, args) =>
        {
            var payload = args.Envelope.Payload;
            var content = payload switch
            {
                TextMessagePayload text => $"Text: {text.Content}",
                SystemEventPayload system => $"System: {system.EventType} - {system.Message}",
                _ => $"Payload: {payload.GetType().Name}"
            };
            Console.WriteLine($"  <- Received: {content}");
        };

        // Start receiving messages in background
        var receiveTask = Task.Run(async () =>
        {
            await foreach (var envelope in connection.IncomingMessages)
            {
                // Messages are also handled by the event above
            }
        });

        // Send some messages
        Console.WriteLine("Sending messages...");

        await connection.SendTextAsync("Hello from client!");
        await Task.Delay(500);

        await connection.SendHeartbeatAsync();
        await Task.Delay(500);

        await connection.SendJsonAsync("""{"action": "test", "value": 42}""");
        await Task.Delay(500);

        // Wait a bit for responses
        await Task.Delay(1000);

        Console.WriteLine("Message channel demo complete.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Message channel demo failed");
        Console.WriteLine($"Error: {ex.Message}");
        Console.WriteLine("Make sure the server is running on https://localhost:5001");
    }
}

static async Task DemoCommandChannelAsync(
    IChannelClientFactory factory,
    ChannelConnectionOptions options,
    ILogger logger)
{
    Console.WriteLine("\n--- Command Channel Demo ---\n");

    try
    {
        await using var channel = (GrpcCommandChannel)await factory.CreateCommandChannelAsync(options);

        Console.WriteLine($"Command channel opened with ID: {channel.ChannelId}");

        // Ping command
        Console.WriteLine("\n1. Ping Command:");
        var (pingSuccess, pingMs) = await channel.PingAsync();
        Console.WriteLine($"   Ping: {(pingSuccess ? "OK" : "Failed")}, RTT: {pingMs}ms");

        // Echo command
        Console.WriteLine("\n2. Echo Command:");
        var echoResult = await channel.EchoAsync("Hello, gRPC!");
        Console.WriteLine($"   Echo: {echoResult}");

        // Status command
        Console.WriteLine("\n3. Status Command:");
        var statusResponse = await channel.ExecuteCommandAsync("status");
        if (statusResponse.IsSuccess && statusResponse.Result is not null)
        {
            var statusJson = System.Text.Encoding.UTF8.GetString(statusResponse.Result);
            Console.WriteLine($"   Status: {statusJson}");
        }

        // Math commands
        Console.WriteLine("\n4. Math Commands:");
        var operations = new[]
        {
            ("add", 10, 5),
            ("subtract", 10, 5),
            ("multiply", 10, 5),
            ("divide", 10, 5)
        };

        foreach (var (op, a, b) in operations)
        {
            var mathResponse = await channel.ExecuteCommandAsync(
                "math",
                new Dictionary<string, string>
                {
                    ["operation"] = op,
                    ["a"] = a.ToString(),
                    ["b"] = b.ToString()
                });

            if (mathResponse.IsSuccess && mathResponse.Result is not null)
            {
                var result = System.Text.Encoding.UTF8.GetString(mathResponse.Result);
                Console.WriteLine($"   {a} {op} {b} = {result}");
            }
        }

        // Delay command with timeout
        Console.WriteLine("\n5. Delay Command (testing timeout):");
        Console.WriteLine("   Sending delay request for 500ms...");
        var delayResponse = await channel.ExecuteCommandAsync(
            "delay",
            new Dictionary<string, string> { ["ms"] = "500" },
            timeoutMs: 2000);
        Console.WriteLine($"   Delay completed in {delayResponse.DurationMs}ms, Status: {delayResponse.Status}");

        // Test timeout
        Console.WriteLine("\n6. Testing Timeout:");
        Console.WriteLine("   Sending delay request for 3000ms with 1000ms timeout...");
        var timeoutResponse = await channel.ExecuteCommandAsync(
            "delay",
            new Dictionary<string, string> { ["ms"] = "3000" },
            timeoutMs: 1000);
        Console.WriteLine($"   Status: {timeoutResponse.Status} (expected: Timeout)");

        Console.WriteLine("\nCommand channel demo complete.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Command channel demo failed");
        Console.WriteLine($"Error: {ex.Message}");
        Console.WriteLine("Make sure the server is running on https://localhost:5001");
    }
}
