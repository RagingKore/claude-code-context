# gRPC Bidirectional Channel Protocol

A modern .NET 10 implementation of a bidirectional gRPC communication protocol featuring real-time messaging and command/response patterns.

## Features

- **Bidirectional Streaming**: Full-duplex communication between client and server
- **Message Channel**: Real-time messaging with support for text, binary, JSON, and system events
- **Command Channel**: Request/response pattern with timeout support and priority levels
- **Modern C# Features**: Records, nullable reference types, primary constructors
- **AOT Compatible**: Native AOT publishing support for both client and server
- **Type-Safe Messaging**: Strongly-typed message envelopes with payload discrimination

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        Protocol Layer                            │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐  │
│  │   Messages      │  │   Contracts     │  │   Converter     │  │
│  │  ChannelEnvelope│  │  IChannelConn   │  │  Proto ↔ Domain │  │
│  │  CommandEnvelope│  │  ICommandChannel│  │                 │  │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                              │
                    ┌─────────┴─────────┐
                    │    gRPC Proto     │
                    │   (channel.proto) │
                    └─────────┬─────────┘
                              │
        ┌─────────────────────┴─────────────────────┐
        │                                           │
┌───────┴───────┐                         ┌─────────┴────────┐
│    Server     │  ←──── Streaming ────→  │     Client       │
│  ChannelSvc   │                         │  ChannelConn     │
│  CommandExec  │                         │  CommandChannel  │
└───────────────┘                         └──────────────────┘
```

## Project Structure

```
prototypes/grpc-bidirectional-channel/
├── src/
│   ├── GrpcChannel.Protocol/       # Shared protocol library
│   │   ├── Messages/               # Domain message types (records)
│   │   ├── Contracts/              # Interfaces and abstractions
│   │   ├── Protos/                 # gRPC proto definitions
│   │   └── MessageConverter.cs     # Proto ↔ Domain conversion
│   ├── GrpcChannel.Server/         # gRPC server implementation
│   │   ├── Services/               # Service implementations
│   │   └── Program.cs              # Server entry point
│   └── GrpcChannel.Client/         # gRPC client implementation
│       └── Program.cs              # Client demo
├── GrpcChannel.sln                 # Solution file
└── README.md                       # This file
```

## Prerequisites

- .NET 10 SDK (Preview)
- HTTPS development certificate

## Getting Started

### 1. Trust the development certificate

```bash
dotnet dev-certs https --trust
```

### 2. Build the solution

```bash
cd prototypes/grpc-bidirectional-channel
dotnet build
```

### 3. Run the server

```bash
dotnet run --project src/GrpcChannel.Server
```

### 4. Run the client (in another terminal)

```bash
dotnet run --project src/GrpcChannel.Client
```

## Protocol Features

### Message Types

| Type | Description |
|------|-------------|
| `Text` | Plain text messages with optional encoding |
| `Binary` | Binary data with content type and optional filename |
| `JSON` | JSON data with optional schema URI |
| `System` | System events (connected, disconnected, error, etc.) |
| `Heartbeat` | Keep-alive messages |
| `Ack` | Acknowledgment messages |
| `Error` | Error messages with code and details |

### Command Priority Levels

| Priority | Description |
|----------|-------------|
| `Low` | Non-urgent commands |
| `Normal` | Default priority |
| `High` | Time-sensitive commands |
| `Critical` | Immediate processing required |

### Built-in Commands

| Command | Parameters | Description |
|---------|------------|-------------|
| `ping` | - | Health check, returns "pong" |
| `echo` | `message` | Echoes the message back |
| `status` | - | Returns server status as JSON |
| `delay` | `ms` | Waits for specified milliseconds |
| `math` | `operation`, `a`, `b` | Performs math operations |

## Code Examples

### Connecting to the Message Channel

```csharp
var options = new ChannelConnectionOptions("https://localhost:5001", "my-client");
var factory = new GrpcChannelClientFactory(loggerFactory);

await using var connection = await factory.CreateConnectionAsync(options);

// Handle incoming messages
connection.MessageReceived += (_, args) =>
{
    Console.WriteLine($"Received: {args.Envelope.Payload}");
};

// Send a text message
await connection.SendTextAsync("Hello, Server!");

// Receive messages
await foreach (var envelope in connection.IncomingMessages)
{
    // Process messages...
}
```

### Using the Command Channel

```csharp
await using var channel = await factory.CreateCommandChannelAsync(options);

// Ping the server
var (success, roundTripMs) = await channel.PingAsync();

// Echo a message
var response = await channel.EchoAsync("Hello!");

// Execute custom command with parameters
var result = await channel.ExecuteCommandAsync(
    "math",
    new Dictionary<string, string>
    {
        ["operation"] = "multiply",
        ["a"] = "6",
        ["b"] = "7"
    },
    timeoutMs: 5000);
```

### Implementing a Custom Command Handler

```csharp
public sealed class MyCommandHandler(ILogger<MyCommandHandler> logger) : ICommandHandler
{
    public string CommandName => "my-command";

    public ValueTask<CommandResponseEnvelope> HandleAsync(
        CommandRequestEnvelope request,
        CancellationToken cancellationToken = default)
    {
        var param = request.Parameters?.GetValueOrDefault("input") ?? "default";

        // Process the command...

        return ValueTask.FromResult(CommandResponseEnvelope.Success(
            request.RequestId,
            Encoding.UTF8.GetBytes($"Processed: {param}")));
    }
}

// Register in DI
builder.Services.AddSingleton<ICommandHandler, MyCommandHandler>();
```

## Modern C# Features Used

### Records with Primary Constructors

```csharp
public sealed record ChannelEnvelope(
    string Id,
    string? CorrelationId,
    IMessagePayload Payload,
    IReadOnlyDictionary<string, string>? Metadata = null,
    DateTimeOffset? TimestampUtc = null);
```

### Nullable Reference Types

```csharp
public interface IConnectionManager
{
    ServerConnection? GetConnection(string connectionId);
}
```

### Primary Constructors for Classes

```csharp
public sealed class ChannelServiceImpl(
    ILogger<ChannelServiceImpl> logger,
    IConnectionManager connectionManager,
    ICommandExecutor commandExecutor) : ChannelService.ChannelServiceBase
{
    // logger, connectionManager, commandExecutor are available as fields
}
```

### Pattern Matching

```csharp
var content = payload switch
{
    TextMessagePayload text => $"Text: {text.Content}",
    SystemEventPayload system => $"System: {system.EventType}",
    HeartbeatPayload => "Heartbeat",
    _ => $"Unknown: {payload.GetType().Name}"
};
```

## AOT Publishing

Both server and client support Native AOT:

```bash
# Publish server as native binary
dotnet publish src/GrpcChannel.Server -c Release -r linux-x64

# Publish client as native binary
dotnet publish src/GrpcChannel.Client -c Release -r linux-x64
```

## License

This is a prototype for experimentation purposes.
