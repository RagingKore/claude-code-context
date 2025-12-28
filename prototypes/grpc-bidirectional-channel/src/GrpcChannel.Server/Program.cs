using System.Text;
using System.Text.Json;
using GrpcChannel.Protocol;
using GrpcChannel.Protocol.Contracts;
using GrpcChannel.Server.Services;

var builder = WebApplication.CreateSlimBuilder(args);

// Configure services
builder.Services.AddGrpc(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaxReceiveMessageSize = 16 * 1024 * 1024; // 16 MB
    options.MaxSendMessageSize = 16 * 1024 * 1024; // 16 MB
});

// Register payload serializer
builder.Services.AddSingleton<IPayloadSerializer>(JsonPayloadSerializer.Default);

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
        return new EchoResponse(request.Message, DateTimeOffset.UtcNow);
    });

    // Ping handler
    channel.OnRequest<PingRequest, PongResponse>("ping", async (request, ctx, ct) =>
    {
        return new PongResponse("pong", DateTimeOffset.UtcNow);
    });

    // Status handler
    channel.OnRequest<StatusRequest, StatusResponse>("status", async (request, ctx, ct) =>
    {
        var channels = registry.GetAllChannels();
        return new StatusResponse(
            channels.Count,
            channels.Select(c => c.ClientId ?? c.ChannelId).ToArray(),
            DateTimeOffset.UtcNow,
            Environment.TickCount64);
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

        return new MathResponse(result, request.Operation, request.A, request.B);
    });

    // Delay handler (for testing timeouts)
    channel.OnRequest<DelayRequest, DelayResponse>("delay", async (request, ctx, ct) =>
    {
        await Task.Delay(request.DelayMs, ct);
        return new DelayResponse(request.DelayMs, DateTimeOffset.UtcNow);
    });

    // Broadcast handler - server can send requests to clients too!
    channel.OnRequest<BroadcastRequest, BroadcastResponse>("broadcast", async (request, ctx, ct) =>
    {
        var sent = 0;
        foreach (var info in registry.GetAllChannels())
        {
            if (info.ChannelId != channel.ChannelId) // Don't send to self
            {
                await info.Channel.SendNotificationAsync("broadcast", request.Message, cancellationToken: ct);
                sent++;
            }
        }
        return new BroadcastResponse(sent);
    });

    // Subscribe to notifications from clients
    channel.OnNotification<ClientNotification>("client.event", async (notification, ctx, ct) =>
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

// Request/Response DTOs
public sealed record EchoRequest(string Message);
public sealed record EchoResponse(string Message, DateTimeOffset Timestamp);

public sealed record PingRequest();
public sealed record PongResponse(string Message, DateTimeOffset Timestamp);

public sealed record StatusRequest();
public sealed record StatusResponse(int ActiveConnections, string[] ClientIds, DateTimeOffset ServerTime, long UptimeMs);

public sealed record MathRequest(string Operation, double A, double B);
public sealed record MathResponse(double Result, string Operation, double A, double B);

public sealed record DelayRequest(int DelayMs);
public sealed record DelayResponse(int DelayedMs, DateTimeOffset CompletedAt);

public sealed record BroadcastRequest(string Message);
public sealed record BroadcastResponse(int SentCount);

public sealed record ClientNotification(string EventType, string Data);
