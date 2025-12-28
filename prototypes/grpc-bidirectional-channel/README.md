# gRPC Duplex Channel Protocol

A modern .NET 10 implementation of a full-duplex gRPC protocol where **both client and server can send requests and receive correlated responses**.

## Features

- **Full Duplex RPC**: Both sides can initiate requests and register handlers
- **Correlation-Based Responses**: Requests are matched to responses via GUID correlation IDs
- **Non-Blocking**: Async handlers with proper cancellation support
- **Handler Registration**: Fluent API for registering typed request handlers
- **Fire-and-Forget Notifications**: Support for one-way messages
- **`google.protobuf.Any` Payloads**: Type-safe, AOT-compatible message payloads
- **RFC 7807 ProblemDetails**: Standardized error responses with detailed problem information
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
│   │   ├── Contracts/              # IDuplexChannel, DuplexResult, ProblemDetails
│   │   ├── Protos/
│   │   │   ├── channel.proto       # Core DuplexMessage with Any + ProblemDetails
│   │   │   └── messages.proto      # Sample request/response message types
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

## Protocol Design

### Single Message Format

The protocol uses a single unified `DuplexMessage` type with `google.protobuf.Any` for payloads:

```protobuf
message DuplexMessage {
  string id = 1;                    // Unique message ID
  string correlation_id = 2;        // Links responses to requests
  string method = 3;                // Handler method name
  google.protobuf.Any payload = 4;  // Type-safe protobuf payload
  StatusCode status = 5;            // Response status
  ProblemDetails problem = 6;       // Error details (RFC 7807)
  int32 timeout_ms = 7;             // Request timeout
  int64 duration_ms = 8;            // Processing time
  int64 timestamp_utc = 9;          // Message timestamp
  map<string, string> headers = 10; // Custom headers
}
```

### Message Type Inference

Message type is inferred from field presence (no explicit type field needed):

| Type | Has `method` | Has `correlation_id` |
|------|--------------|---------------------|
| **Request** | ✓ | ✓ |
| **Response** | ✗ | ✓ |
| **Notification** | ✓ | ✗ |

### ProblemDetails (RFC 7807)

Error responses include structured problem details:

```protobuf
message ProblemDetails {
  string type = 1;                    // URI reference (e.g., "urn:grpc:duplex:timeout")
  string title = 2;                   // Human-readable summary
  int32 status = 3;                   // Status code
  string detail = 4;                  // Detailed explanation
  string instance = 5;                // Occurrence URI
  string code = 6;                    // Application error code
  string trace = 7;                   // Stack trace (debug only)
  map<string, string> extensions = 8; // Additional properties
  repeated ProblemDetails errors = 9; // Nested errors (for chains)
}
```

### Status Codes

| Status | Code | Description |
|--------|------|-------------|
| `Ok` | 1 | Request completed successfully |
| `Error` | 2 | General error occurred |
| `NotFound` | 3 | Method/handler not found |
| `Timeout` | 4 | Request timed out |
| `Cancelled` | 5 | Request was cancelled |
| `Unauthorized` | 6 | Not authorized |
| `InvalidRequest` | 7 | Request was malformed |
| `Unavailable` | 8 | Service unavailable |
| `Internal` | 9 | Internal error |

## Code Examples

### Client: Sending Requests to Server

```csharp
await using var client = new DuplexClient(options);
await client.ConnectAsync();

// Send typed request, await typed response
var result = await client.Channel.SendRequestAsync<EchoRequest, EchoResponse>(
    "echo",
    new EchoRequest { Message = "Hello!" });

if (result.IsSuccess)
{
    Console.WriteLine($"Echo: {result.Value!.Message}");
    Console.WriteLine($"RTT: {result.DurationMs}ms");
}
else
{
    // Access RFC 7807 problem details
    Console.WriteLine($"Error: {result.Problem?.Detail}");
    Console.WriteLine($"Code: {result.Problem?.Code}");
}

// With timeout
var mathResult = await client.Channel.SendRequestAsync<MathRequest, MathResponse>(
    "math",
    new MathRequest { Operation = "multiply", A = 6, B = 7 },
    timeoutMs: 5000);
```

### Client: Registering Handlers (Server → Client)

```csharp
// Server can now call methods on the client!
client.Channel.OnRequest<GetClientInfoRequest, ClientInfoResponse>(
    "client.info",
    async (request, ctx, ct) =>
    {
        return new ClientInfoResponse
        {
            MachineName = Environment.MachineName,
            ProcessId = Environment.ProcessId,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    });

// Handle notifications
client.Channel.OnNotification<BroadcastNotification>(
    "broadcast",
    async (notification, ctx, ct) =>
    {
        Console.WriteLine($"Broadcast: {notification.Message}");
    });
```

### Server: Registering Handlers (Client → Server)

```csharp
registry.OnAllChannels(channel =>
{
    channel.OnRequest<EchoRequest, EchoResponse>("echo", async (req, ctx, ct) =>
    {
        return new EchoResponse
        {
            Message = req.Message,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    });

    channel.OnRequest<MathRequest, MathResponse>("math", async (req, ctx, ct) =>
    {
        var result = req.Operation switch
        {
            "add" => req.A + req.B,
            "multiply" => req.A * req.B,
            _ => double.NaN
        };
        return new MathResponse { Result = result, Operation = req.Operation };
    });
});
```

### Server: Sending Requests to Client

```csharp
// Get a specific client channel
var clientChannel = registry.GetChannelByClientId("client-123");

// Server initiates request to client
var result = await clientChannel!.SendRequestAsync<GetClientInfoRequest, ClientInfoResponse>(
    "client.info",
    new GetClientInfoRequest());

if (result.IsSuccess)
{
    Console.WriteLine($"Client machine: {result.Value!.MachineName}");
}
```

### Fire-and-Forget Notifications

```csharp
// Send notification (no response)
await channel.SendNotificationAsync("client.event",
    new ClientEventNotification
    {
        EventType = "startup",
        Data = "Ready"
    });

// Broadcast to all clients
foreach (var client in registry.GetAllChannels())
{
    await client.Channel.SendNotificationAsync("broadcast",
        new BroadcastNotification { Message = "Hello all!" });
}
```

## How Correlation Works

```
Client                                Server
  │                                     │
  │──── DuplexMessage ─────────────────▶│
  │     id: "msg-456"                  │
  │     correlation_id: "abc123"       │
  │     method: "echo"                 │
  │     payload: Any<EchoRequest>      │
  │                                     │
  │     [Client stores pending          │
  │      request with "abc123"]         │
  │                                     │
  │                                     │──── Handler invoked
  │                                     │     for "echo" method
  │                                     │     Unpacks Any<EchoRequest>
  │                                     │
  │◀──── DuplexMessage ────────────────│
  │      id: "msg-789"                  │
  │      correlation_id: "abc123"       │
  │      status: OK                     │
  │      payload: Any<EchoResponse>     │
  │                                     │
  │     [Client matches "abc123"        │
  │      completes awaiting Task]       │
  │                                     │
```

## Modern C# Features Used

### Protobuf Messages with `google.protobuf.Any`

```protobuf
// messages.proto - Sample message definitions
message EchoRequest {
  string message = 1;
}

message EchoResponse {
  string message = 1;
  int64 timestamp_utc = 2;
}

message MathRequest {
  string operation = 1;
  double a = 2;
  double b = 3;
}
```

### Primary Constructors

```csharp
public sealed class DuplexClient(
    DuplexClientOptions options,
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
    return new TResponse { /* ... */ };
});
```

### Result Types with ProblemDetails

```csharp
var result = await channel.SendRequestAsync<Req, Res>("method", request);

if (result.IsSuccess)
{
    var value = result.Value!;
}
else
{
    // RFC 7807 compliant error details
    Console.WriteLine($"Status: {result.Status}");
    Console.WriteLine($"Title: {result.Problem?.Title}");
    Console.WriteLine($"Detail: {result.Problem?.Detail}");
    Console.WriteLine($"Code: {result.Problem?.Code}");
}

// Or throw on failure
var value = result.GetValueOrThrow();  // Throws DuplexException
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
