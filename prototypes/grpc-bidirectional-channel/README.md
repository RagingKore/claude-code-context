# gRPC Duplex Channel Protocol

A modern .NET 10 implementation of a full-duplex gRPC protocol where **both client and server can send requests and receive correlated responses**.

## Features

- **Full Duplex RPC**: Both sides can initiate requests and register handlers
- **Correlation-Based Responses**: Requests are matched to responses via GUID correlation IDs
- **Non-Blocking**: Async handlers with proper cancellation support
- **Handler Registration**: Fluent API for registering typed request handlers
- **Fire-and-Forget Notifications**: Support for one-way messages
- **Modern C# Features**: Records, nullable reference types, primary constructors
- **AOT Compatible**: Native AOT publishing support
- **Modern Solution Format**: Uses the new `.slnx` XML-based solution file format

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                         DuplexChannel                                │
│  ┌─────────────────────────────────────────────────────────────────┐│
│  │  • SendRequestAsync<TReq, TRes>() → awaits correlated response  ││
│  │  • OnRequest<TReq, TRes>() → registers handler                  ││
│  │  • SendNotificationAsync() → fire-and-forget                    ││
│  │  • OnNotification() → registers notification handler            ││
│  └─────────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────────┘
                                  │
                    ┌─────────────┴─────────────┐
                    │   gRPC Bidirectional      │
                    │   Streaming (Open)        │
                    └─────────────┬─────────────┘
                                  │
        ┌─────────────────────────┴─────────────────────────┐
        ▼                                                   ▼
┌───────────────┐                                 ┌───────────────┐
│    Server     │  ←───── Request/Response ─────→ │    Client     │
│               │                                 │               │
│ Can SEND:     │                                 │ Can SEND:     │
│ • Requests    │                                 │ • Requests    │
│ • Responses   │                                 │ • Responses   │
│ • Notifs      │                                 │ • Notifs      │
│               │                                 │               │
│ Can HANDLE:   │                                 │ Can HANDLE:   │
│ • Requests    │                                 │ • Requests    │
│ • Notifs      │                                 │ • Notifs      │
└───────────────┘                                 └───────────────┘
```

## Project Structure

```
prototypes/grpc-bidirectional-channel/
├── src/
│   ├── GrpcChannel.Protocol/       # Shared protocol library
│   │   ├── Contracts/              # IDuplexChannel, IPayloadSerializer
│   │   ├── Protos/                 # gRPC proto definitions
│   │   └── DuplexChannel.cs        # Core channel implementation
│   ├── GrpcChannel.Server/         # gRPC server implementation
│   │   ├── Services/               # DuplexServiceImpl, ConnectionRegistry
│   │   └── Program.cs              # Server with handler registration
│   └── GrpcChannel.Client/         # gRPC client implementation
│       ├── DuplexClient.cs         # Client wrapper
│       └── Program.cs              # Client demo with handlers
├── GrpcChannel.slnx                # Solution file (XML format)
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

## Protocol Messages

| Type | Description |
|------|-------------|
| `Request` | Expects a correlated response (has `correlation_id`) |
| `Response` | Correlates to a request via `correlation_id` |
| `Notification` | Fire-and-forget, no response expected |

### Response Status Codes

| Status | Description |
|--------|-------------|
| `Ok` | Request completed successfully |
| `Error` | General error occurred |
| `NotFound` | Method/handler not found |
| `Timeout` | Request timed out |
| `Cancelled` | Request was cancelled |
| `Unauthorized` | Not authorized |
| `InvalidRequest` | Request was malformed |

## Code Examples

### Client: Sending Requests to Server

```csharp
await using var client = new DuplexClient(options, serializer);
await client.ConnectAsync();

// Send typed request, await typed response
var result = await client.Channel.SendRequestAsync<EchoRequest, EchoResponse>(
    "echo",
    new EchoRequest("Hello!"));

if (result.IsSuccess)
{
    Console.WriteLine($"Echo: {result.Value!.Message}");
    Console.WriteLine($"RTT: {result.DurationMs}ms");
}

// With timeout
var mathResult = await client.Channel.SendRequestAsync<MathRequest, MathResponse>(
    "math",
    new MathRequest("multiply", 6, 7),
    timeoutMs: 5000);
```

### Client: Registering Handlers (Server → Client)

```csharp
// Server can now call methods on the client!
client.Channel.OnRequest<GetClientInfoRequest, ClientInfoResponse>(
    "client.info",
    async (request, ctx, ct) =>
    {
        return new ClientInfoResponse(
            Environment.MachineName,
            Environment.ProcessId);
    });

// Handle notifications
client.Channel.OnNotification<BroadcastMessage>(
    "broadcast",
    async (message, ctx, ct) =>
    {
        Console.WriteLine($"Broadcast: {message.Text}");
    });
```

### Server: Registering Handlers (Client → Server)

```csharp
registry.OnAllChannels(channel =>
{
    channel.OnRequest<EchoRequest, EchoResponse>("echo", async (req, ctx, ct) =>
    {
        return new EchoResponse(req.Message, DateTimeOffset.UtcNow);
    });

    channel.OnRequest<MathRequest, MathResponse>("math", async (req, ctx, ct) =>
    {
        var result = req.Operation switch
        {
            "add" => req.A + req.B,
            "multiply" => req.A * req.B,
            _ => double.NaN
        };
        return new MathResponse(result);
    });
});
```

### Server: Sending Requests to Client

```csharp
// Get a specific client channel
var clientChannel = registry.GetChannelByClientId("client-123");

// Server initiates request to client
var info = await clientChannel!.SendRequestAsync<GetClientInfoRequest, ClientInfoResponse>(
    "client.info",
    new GetClientInfoRequest());

Console.WriteLine($"Client machine: {info.Value!.MachineName}");
```

### Fire-and-Forget Notifications

```csharp
// Send notification (no response)
await channel.SendNotificationAsync("client.event", new EventData("startup"));

// Broadcast to all clients
foreach (var client in registry.GetAllChannels())
{
    await client.Channel.SendNotificationAsync("broadcast", new Message("Hello all!"));
}
```

## How Correlation Works

```
Client                                Server
  │                                     │
  │──── Request ────────────────────────▶│
  │     correlation_id: "abc123"        │
  │     method: "echo"                  │
  │     payload: {...}                  │
  │                                     │
  │     [Client stores pending          │
  │      request with "abc123"]         │
  │                                     │
  │                                     │──── Handler invoked
  │                                     │     for "echo" method
  │                                     │
  │◀──── Response ──────────────────────│
  │      correlation_id: "abc123"       │
  │      status: OK                     │
  │      payload: {...}                 │
  │                                     │
  │     [Client matches "abc123"        │
  │      completes awaiting Task]       │
  │                                     │
```

## Modern C# Features Used

### Records for DTOs

```csharp
public sealed record EchoRequest(string Message);
public sealed record EchoResponse(string Message, DateTimeOffset Timestamp);

public sealed record MathRequest(string Operation, double A, double B);
public sealed record MathResponse(double Result);
```

### Primary Constructors

```csharp
public sealed class DuplexClient(
    DuplexClientOptions options,
    IPayloadSerializer serializer,
    ILogger<DuplexClient>? logger = null) : IAsyncDisposable
{
    // Fields available directly from constructor parameters
}
```

### Handler Registration with Lambdas

```csharp
channel.OnRequest<TRequest, TResponse>("method", async (request, context, ct) =>
{
    // Non-blocking async handler
    return new TResponse(...);
});
```

### Result Types

```csharp
var result = await channel.SendRequestAsync<Req, Res>("method", request);

if (result.IsSuccess)
{
    var value = result.Value!;
}
else
{
    Console.WriteLine($"Failed: {result.Status} - {result.Error}");
}

// Or throw on failure
var value = result.GetValueOrThrow();
```

## Built-in Server Methods

| Method | Request | Response | Description |
|--------|---------|----------|-------------|
| `ping` | `PingRequest` | `PongResponse` | Health check |
| `echo` | `EchoRequest` | `EchoResponse` | Echo message back |
| `status` | `StatusRequest` | `StatusResponse` | Server status |
| `math` | `MathRequest` | `MathResponse` | Math operations |
| `delay` | `DelayRequest` | `DelayResponse` | Delay for testing |
| `broadcast` | `BroadcastRequest` | `BroadcastResponse` | Broadcast to clients |

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
