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
    // Echo handler
    channel.OnRequest<EchoRequest, EchoResponse>("echo", async (request, ctx, ct) =>
    {
        app.Logger.LogDebug("Echo request from {RemoteId}: {Message}", ctx.RemoteId, request.Message);
        return new EchoResponse
        {
            Message = request.Message,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    });

    // Ping handler
    channel.OnRequest<PingRequest, PongResponse>("ping", async (request, ctx, ct) =>
    {
        return new PongResponse
        {
            Message = "pong",
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    });

    // Status handler
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

    // Math handler
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

    // Delay handler (for testing timeouts)
    channel.OnRequest<DelayRequest, DelayResponse>("delay", async (request, ctx, ct) =>
    {
        await Task.Delay(request.DelayMs, ct);
        return new DelayResponse
        {
            DelayedMs = request.DelayMs,
            CompletedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    });

    // Broadcast handler - server can send requests to clients too!
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

    // Subscribe to notifications from clients
    channel.OnNotification<ClientEventNotification>("client.event", async (notification, ctx, ct) =>
    {
        app.Logger.LogInformation("Received notification from {RemoteId}: {EventType} - {Data}",
            ctx.RemoteId, notification.EventType, notification.Data);
    });
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
app.Logger.LogInformation("Available methods: echo, ping, status, math, delay, broadcast");

await app.RunAsync();
