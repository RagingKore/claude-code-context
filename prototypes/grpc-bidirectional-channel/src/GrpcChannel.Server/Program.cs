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

// Register connection manager as singleton
builder.Services.AddSingleton<IConnectionManager, ConnectionManager>();

// Register command handlers
builder.Services.AddSingleton<ICommandHandler, EchoCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, PingCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, StatusCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, DelayCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, MathCommandHandler>();

// Register command executor
builder.Services.AddSingleton<ICommandExecutor, CommandExecutorService>();

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

// Map gRPC services
app.MapGrpcService<ChannelServiceImpl>();

// Health check endpoint
app.MapGet("/", () => Results.Ok(new
{
    Service = "GrpcChannel.Server",
    Status = "Running",
    Timestamp = DateTimeOffset.UtcNow
}));

// Startup message
app.Logger.LogInformation("gRPC Channel Server starting on https://localhost:5001");
app.Logger.LogInformation("Available commands: echo, ping, status, delay, math");

await app.RunAsync();
