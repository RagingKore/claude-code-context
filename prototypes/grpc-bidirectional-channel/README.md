# gRPC Duplex Channel Protocol

A modern .NET 10 implementation of a full-duplex gRPC protocol where **both client and server can send requests and receive correlated responses**.

## Features

- **Full Duplex RPC**: Both sides can initiate requests and register handlers
- **Correlation-Based Responses**: Requests are matched to responses via GUID correlation IDs
- **Non-Blocking**: Async handlers with proper cancellation support
- **Handler Registration**: Fluent API for registering typed request handlers
- **Fire-and-Forget Notifications**: Support for one-way messages
- **Dual Payload Support**: Use protobuf messages OR C# records (JSON serialized)
- **`google.protobuf.Any` Payloads**: Type-safe, AOT-compatible message payloads
- **RFC 7807 ProblemDetails**: Standardized error responses with detailed problem information
- **Modern C# Features**: Records, nullable reference types, primary constructors
- **AOT Compatible**: Native AOT publishing support
- **Modern Solution Format**: Uses the new `.slnx` XML-based solution file format

## Payload Types

The protocol supports two payload types that can be used interchangeably:

### 1. Protobuf Messages (Direct)
For maximum performance and type safety, use protobuf messages defined in `.proto` files:

```protobuf
// messages.proto
message EchoRequest {
  string message = 1;
}

message EchoResponse {
  string message = 1;
  int64 timestamp_utc = 2;
}
```

```csharp
// Packed directly into google.protobuf.Any
var result = await channel.SendRequestAsync<EchoRequest, EchoResponse>(
    "echo",
    new EchoRequest { Message = "Hello!" });
```

### 2. C# Records (JSON Serialized)
For convenience and rapid prototyping, use regular C# types serialized via JSON:

```csharp
// Define as C# records
public sealed record GreetingRequest(string Name, string? Language = null);
public sealed record GreetingResponse(string Greeting, DateTimeOffset Timestamp);

// Automatically serialized to JSON, wrapped in RawPayload, then packed into Any
var result = await channel.SendRequestAsync<GreetingRequest, GreetingResponse>(
    "greet",
    new GreetingRequest("World", "spanish"));
```

### How It Works

```
┌────────────────────────────────────────────────────────────────────────┐
│  Payload Detection (automatic)                                         │
├────────────────────────────────────────────────────────────────────────┤
│                                                                        │
│  Is payload a protobuf IMessage?                                       │
│    YES → Any.Pack(payload)           ← Direct protobuf, best perf     │
│    NO  → Serialize to JSON                                             │
│          → Wrap in RawPayload { data, type_name, content_type }       │
│          → Any.Pack(rawPayload)      ← Flexible, any C# type          │
│                                                                        │
└────────────────────────────────────────────────────────────────────────┘
```

The `RawPayload` wrapper message:
```protobuf
message RawPayload {
  bytes data = 1;           // Serialized payload (e.g., JSON bytes)
  string type_name = 2;     // "MyNamespace.MyRecord"
  string content_type = 3;  // "application/json"
}
```

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
│ Protobuf OR   │                                 │ Protobuf OR   │
│ C# Records    │                                 │ C# Records    │
└───────────────┘                                 └───────────────┘
```

## Project Structure

```
prototypes/grpc-bidirectional-channel/
├── src/
│   ├── GrpcChannel.Protocol/       # Shared protocol library
│   │   ├── Contracts/              # IDuplexChannel, IPayloadSerializer
│   │   ├── Protos/
│   │   │   ├── channel.proto       # DuplexMessage, RawPayload, ProblemDetails
│   │   │   └── messages.proto      # Sample protobuf message types
│   │   ├── DuplexChannel.cs        # Core channel implementation
│   │   └── JsonPayloadSerializer.cs # Default JSON serializer
│   ├── GrpcChannel.Server/         # gRPC server implementation
│   │   ├── Services/               # DuplexServiceImpl, ConnectionRegistry
│   │   └── Program.cs              # Server with protobuf + record handlers
│   └── GrpcChannel.Client/         # gRPC client implementation
│       ├── DuplexClient.cs         # Client wrapper
│       └── Program.cs              # Client demo with both payload types
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

```protobuf
message DuplexMessage {
  string id = 1;                    // Unique message ID
  string correlation_id = 2;        // Links responses to requests
  string method = 3;                // Handler method name
  google.protobuf.Any payload = 4;  // Protobuf message OR RawPayload wrapper
  StatusCode status = 5;            // Response status
  ProblemDetails problem = 6;       // Error details (RFC 7807)
  int32 timeout_ms = 7;             // Request timeout
  int64 duration_ms = 8;            // Processing time
  int64 timestamp_utc = 9;          // Message timestamp
  map<string, string> headers = 10; // Custom headers
}
```

### Message Type Inference

| Type | Has `method` | Has `correlation_id` |
|------|--------------|---------------------|
| **Request** | ✓ | ✓ |
| **Response** | ✗ | ✓ |
| **Notification** | ✓ | ✗ |

### ProblemDetails (RFC 7807)

```protobuf
message ProblemDetails {
  string type = 1;                    // URI reference
  string title = 2;                   // Human-readable summary
  int32 status = 3;                   // Status code
  string detail = 4;                  // Detailed explanation
  string instance = 5;                // Occurrence URI
  string code = 6;                    // Application error code
  string trace = 7;                   // Stack trace (debug only)
  map<string, string> extensions = 8; // Additional properties
  repeated ProblemDetails errors = 9; // Nested errors
}
```

## Code Examples

### Protobuf Messages

```csharp
// Server handler (protobuf)
channel.OnRequest<EchoRequest, EchoResponse>("echo", async (req, ctx, ct) =>
{
    return new EchoResponse
    {
        Message = req.Message,
        TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };
});

// Client call (protobuf)
var result = await channel.SendRequestAsync<EchoRequest, EchoResponse>(
    "echo",
    new EchoRequest { Message = "Hello!" });
```

### C# Records (JSON)

```csharp
// Define records
public sealed record GreetingRequest(string Name, string? Language = null);
public sealed record GreetingResponse(string Greeting, DateTimeOffset Timestamp);

// Server handler (JSON)
channel.OnRequest<GreetingRequest, GreetingResponse>("greet", async (req, ctx, ct) =>
{
    var greeting = req.Language switch
    {
        "spanish" => $"¡Hola, {req.Name}!",
        "french" => $"Bonjour, {req.Name}!",
        _ => $"Hello, {req.Name}!"
    };
    return new GreetingResponse(greeting, DateTimeOffset.UtcNow);
});

// Client call (JSON)
var result = await channel.SendRequestAsync<GreetingRequest, GreetingResponse>(
    "greet",
    new GreetingRequest("World", "spanish"));

Console.WriteLine(result.Value!.Greeting); // "¡Hola, World!"
```

### Complex Nested Records

```csharp
// Nested record types
public sealed record ProcessDataRequest(List<string> Items);
public sealed record ProcessDataResponse(
    List<ProcessedItem> Results,
    int TotalProcessed,
    DateTimeOffset ProcessedAt);
public sealed record ProcessedItem(
    int Id,
    string OriginalValue,
    string ProcessedValue,
    int Length);

// Handler with complex types
channel.OnRequest<ProcessDataRequest, ProcessDataResponse>("process", async (req, ctx, ct) =>
{
    var results = req.Items
        .Select((item, i) => new ProcessedItem(i + 1, item, item.ToUpper(), item.Length))
        .ToList();

    return new ProcessDataResponse(results, results.Count, DateTimeOffset.UtcNow);
});
```

### Custom Serializer

```csharp
// Implement custom serializer
public class MessagePackSerializer : IPayloadSerializer
{
    public string ContentType => "application/x-msgpack";

    public byte[] Serialize<T>(T value) => MessagePackSerializer.Serialize(value);
    public T Deserialize<T>(byte[] data) => MessagePackSerializer.Deserialize<T>(data);
    public object Deserialize(byte[] data, Type type) => MessagePackSerializer.Deserialize(type, data);
}

// Use with client
var client = new DuplexClient(options, new MessagePackSerializer());

// Use with server (via DI)
services.AddSingleton<IPayloadSerializer, MessagePackSerializer>();
```

## Built-in Methods

### Protobuf Methods
| Method | Request | Response | Description |
|--------|---------|----------|-------------|
| `ping` | `PingRequest` | `PongResponse` | Health check |
| `echo` | `EchoRequest` | `EchoResponse` | Echo message |
| `status` | `StatusRequest` | `StatusResponse` | Server status |
| `math` | `MathRequest` | `MathResponse` | Math operations |
| `delay` | `DelayRequest` | `DelayResponse` | Delay for testing |
| `broadcast` | `BroadcastRequest` | `BroadcastResponse` | Broadcast to clients |

### C# Record Methods
| Method | Request | Response | Description |
|--------|---------|----------|-------------|
| `greet` | `GreetingRequest` | `GreetingResponse` | Localized greeting |
| `process` | `ProcessDataRequest` | `ProcessDataResponse` | Data processing |
| `user.get` | `GetUserRequest` | `UserInfo` | Get user info |

## Error Handling

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

## AOT Publishing

Both server and client support Native AOT:

```bash
# Publish server as native binary
dotnet publish src/GrpcChannel.Server -c Release -r linux-x64

# Publish client as native binary
dotnet publish src/GrpcChannel.Client -c Release -r linux-x64
```

For full AOT support with JSON serialization, use source generators:

```csharp
[JsonSerializable(typeof(GreetingRequest))]
[JsonSerializable(typeof(GreetingResponse))]
public partial class AppJsonContext : JsonSerializerContext { }

var serializer = new JsonPayloadSerializer(AppJsonContext.Default);
var client = new DuplexClient(options, serializer);
```

## License

This is a prototype for experimentation purposes.
