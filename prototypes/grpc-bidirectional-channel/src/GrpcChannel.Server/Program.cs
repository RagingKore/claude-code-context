using GrpcChannel.Protocol.Messages;
using GrpcChannel.Server.Services;

var builder = WebApplication.CreateSlimBuilder(args);

// Configure services
builder.Services.AddGrpc(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaxReceiveMessageSize = 16 * 1024 * 1024; // 16 MB
    options.MaxSendMessageSize = 16 * 1024 * 1024; // 16 MB
});

// Register connection registry
builder.Services.AddSingleton<IConnectionRegistry, ConnectionRegistry>();

// Register data stream registry
builder.Services.AddSingleton<IDataStreamRegistry, DataStreamRegistry>();

// Configure Kestrel for HTTP/2
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5001, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
        listenOptions.UseHttps();
    });
});

var app = builder.Build();

// Register request handlers on all channels
var registry = app.Services.GetRequiredService<IConnectionRegistry>();

// Register handlers that will be applied to all client channels
registry.OnAllChannels(channel =>
{
    // =============================================
    // PROTOBUF MESSAGE HANDLERS (from messages.proto)
    // =============================================

    // Echo handler - uses protobuf messages
    channel.OnRequest<EchoRequest, EchoResponse>("echo", async (request, ctx, ct) =>
    {
        app.Logger.LogDebug("[Proto] Echo request from {RemoteId}: {Message}", ctx.RemoteId, request.Message);
        return new EchoResponse
        {
            Message = request.Message,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    });

    // Ping handler - uses protobuf messages
    channel.OnRequest<PingRequest, PongResponse>("ping", async (request, ctx, ct) =>
    {
        return new PongResponse
        {
            Message = "pong",
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    });

    // Status handler - uses protobuf messages
    channel.OnRequest<StatusRequest, StatusResponse>("status", async (request, ctx, ct) =>
    {
        var channels = registry.GetAllChannels();
        var response = new StatusResponse
        {
            ActiveConnections = channels.Count,
            ServerTimeUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UptimeMs = Environment.TickCount64
        };
        response.ClientIds.AddRange(channels.Select(c => c.ClientId ?? c.ChannelId));
        return response;
    });

    // Math handler - uses protobuf messages
    channel.OnRequest<MathRequest, MathResponse>("math", async (request, ctx, ct) =>
    {
        var result = request.Operation.ToLowerInvariant() switch
        {
            "add" => request.A + request.B,
            "subtract" or "sub" => request.A - request.B,
            "multiply" or "mul" => request.A * request.B,
            "divide" or "div" when request.B != 0 => request.A / request.B,
            _ => double.NaN
        };

        return new MathResponse
        {
            Result = result,
            Operation = request.Operation,
            A = request.A,
            B = request.B
        };
    });

    // Delay handler - uses protobuf messages
    channel.OnRequest<DelayRequest, DelayResponse>("delay", async (request, ctx, ct) =>
    {
        await Task.Delay(request.DelayMs, ct);
        return new DelayResponse
        {
            DelayedMs = request.DelayMs,
            CompletedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    });

    // Broadcast handler - uses protobuf messages
    channel.OnRequest<BroadcastRequest, BroadcastResponse>("broadcast", async (request, ctx, ct) =>
    {
        var sent = 0;
        var notification = new BroadcastNotification
        {
            Message = request.Message,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        foreach (var info in registry.GetAllChannels())
        {
            if (info.ChannelId != channel.ChannelId) // Don't send to self
            {
                await info.Channel.SendNotificationAsync("broadcast", notification, cancellationToken: ct);
                sent++;
            }
        }
        return new BroadcastResponse { SentCount = sent };
    });

    // Subscribe to notifications from clients - uses protobuf messages
    channel.OnNotification<ClientEventNotification>("client.event", async (notification, ctx, ct) =>
    {
        app.Logger.LogInformation("[Proto] Received notification from {RemoteId}: {EventType} - {Data}",
            ctx.RemoteId, notification.EventType, notification.Data);
    });

    // =============================================
    // C# RECORD HANDLERS (serialized via JSON)
    // =============================================

    // Greeting handler - uses C# records (serialized with JSON)
    channel.OnRequest<GreetingRequest, GreetingResponse>("greet", async (request, ctx, ct) =>
    {
        app.Logger.LogDebug("[JSON] Greeting request from {RemoteId}: {Name}", ctx.RemoteId, request.Name);
        var greeting = request.Language?.ToLowerInvariant() switch
        {
            "spanish" or "es" => $"¡Hola, {request.Name}!",
            "french" or "fr" => $"Bonjour, {request.Name}!",
            "german" or "de" => $"Hallo, {request.Name}!",
            "japanese" or "ja" => $"こんにちは、{request.Name}さん！",
            _ => $"Hello, {request.Name}!"
        };

        return new GreetingResponse(greeting, DateTimeOffset.UtcNow);
    });

    // Complex data handler - uses C# records with nested types
    channel.OnRequest<ProcessDataRequest, ProcessDataResponse>("process", async (request, ctx, ct) =>
    {
        app.Logger.LogDebug("[JSON] Processing data: {Items} items", request.Items.Count);

        var results = request.Items
            .Select((item, index) => new ProcessedItem(
                Id: index + 1,
                OriginalValue: item,
                ProcessedValue: item.ToUpperInvariant(),
                Length: item.Length))
            .ToList();

        return new ProcessDataResponse(
            Results: results,
            TotalProcessed: results.Count,
            ProcessedAt: DateTimeOffset.UtcNow);
    });

    // User info handler - demonstrates record with multiple properties
    channel.OnRequest<GetUserRequest, UserInfo>("user.get", async (request, ctx, ct) =>
    {
        // Simulate user lookup
        return new UserInfo(
            Id: request.UserId,
            Name: $"User {request.UserId}",
            Email: $"user{request.UserId}@example.com",
            Roles: ["user", "member"],
            Metadata: new Dictionary<string, string>
            {
                ["source"] = "demo",
                ["channel"] = ctx.RemoteId ?? "unknown"
            },
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-30));
    });

    // Subscribe to custom record notifications
    channel.OnNotification<CustomEventRecord>("custom.event", async (notification, ctx, ct) =>
    {
        app.Logger.LogInformation("[JSON] Custom event from {RemoteId}: {Type} - {Payload}",
            ctx.RemoteId, notification.EventType, notification.Payload);
    });
});

// =============================================
// DATA STREAM HANDLERS (high-throughput server-streaming)
// =============================================

var dataStreamRegistry = app.Services.GetRequiredService<IDataStreamRegistry>();

// Counter stream - sends incrementing numbers at high rate
dataStreamRegistry.Register("counter", async (context, writer, ct) =>
{
    app.Logger.LogInformation("Starting counter stream {StreamId}", context.StreamId);

    var count = 0L;
    var maxRate = context.MaxRate > 0 ? context.MaxRate : 1000;
    var delay = TimeSpan.FromMilliseconds(1000.0 / maxRate);

    while (!ct.IsCancellationRequested)
    {
        await writer.WriteAsync(new CounterEvent(++count, DateTimeOffset.UtcNow));

        if (delay.TotalMilliseconds > 0)
        {
            await Task.Delay(delay, ct);
        }
    }
});

// Events stream - simulated event stream
dataStreamRegistry.Register("events", async (context, writer, ct) =>
{
    app.Logger.LogInformation("Starting events stream {StreamId} with filter: {Filter}",
        context.StreamId, context.Filter);

    var eventTypes = new[] { "user.login", "user.logout", "order.created", "order.completed", "system.alert" };
    var sequence = 0L;

    while (!ct.IsCancellationRequested)
    {
        var eventType = eventTypes[Random.Shared.Next(eventTypes.Length)];

        // Apply filter if specified
        if (!string.IsNullOrEmpty(context.Filter) && !eventType.StartsWith(context.Filter))
        {
            await Task.Delay(10, ct);
            continue;
        }

        await writer.WriteAsync(
            new StreamEvent(++sequence, eventType, $"Event data for {eventType}", DateTimeOffset.UtcNow),
            partitionKey: eventType.Split('.')[0],
            messageType: eventType);

        await Task.Delay(Random.Shared.Next(10, 100), ct);
    }
});

// Batch stream - sends data in batches then completes
dataStreamRegistry.Register("batch", async (context, writer, ct) =>
{
    var batchSize = 100;
    if (context.Options.TryGetValue("batch_size", out var sizeStr) && int.TryParse(sizeStr, out var size))
    {
        batchSize = size;
    }

    app.Logger.LogInformation("Starting batch stream {StreamId} with size: {Size}", context.StreamId, batchSize);

    for (var i = 1; i <= batchSize && !ct.IsCancellationRequested; i++)
    {
        await writer.WriteAsync(new BatchItem(i, $"Item {i}", i * 1.5));
    }

    // Stream will auto-complete when handler returns
});

// Map gRPC services
app.MapGrpcService<DuplexServiceImpl>();

// Health check endpoint
app.MapGet("/", () => Results.Ok(new
{
    Service = "GrpcChannel.Server",
    Status = "Running",
    Timestamp = DateTimeOffset.UtcNow
}));

// Startup message
app.Logger.LogInformation("gRPC Duplex Server starting on https://localhost:5001");
app.Logger.LogInformation("Protobuf methods: echo, ping, status, math, delay, broadcast");
app.Logger.LogInformation("C# record methods: greet, process, user.get");
app.Logger.LogInformation("Data streams: counter, events, batch");

await app.RunAsync();

// =============================================
// C# RECORD TYPES (serialized with JSON via RawPayload)
// =============================================

// Greeting request/response
public sealed record GreetingRequest(string Name, string? Language = null);
public sealed record GreetingResponse(string Greeting, DateTimeOffset Timestamp);

// Complex data processing
public sealed record ProcessDataRequest(List<string> Items);
public sealed record ProcessDataResponse(List<ProcessedItem> Results, int TotalProcessed, DateTimeOffset ProcessedAt);
public sealed record ProcessedItem(int Id, string OriginalValue, string ProcessedValue, int Length);

// User info
public sealed record GetUserRequest(int UserId);
public sealed record UserInfo(
    int Id,
    string Name,
    string Email,
    List<string> Roles,
    Dictionary<string, string> Metadata,
    DateTimeOffset CreatedAt);

// Custom event notification
public sealed record CustomEventRecord(string EventType, object? Payload, DateTimeOffset Timestamp);

// =============================================
// DATA STREAM EVENT TYPES
// =============================================

// Counter stream event
public sealed record CounterEvent(long Count, DateTimeOffset Timestamp);

// Generic stream event
public sealed record StreamEvent(long Sequence, string EventType, string Data, DateTimeOffset Timestamp);

// Batch item
public sealed record BatchItem(int Id, string Name, double Value);
