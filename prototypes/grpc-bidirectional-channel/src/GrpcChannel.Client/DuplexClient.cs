using Grpc.Core;
using Grpc.Net.Client;
using GrpcChannel.Protocol;
using GrpcChannel.Protocol.Contracts;
using GrpcChannel.Protocol.Protos;
using Microsoft.Extensions.Logging;

namespace GrpcChannel.Client;

/// <summary>
/// Duplex client that connects to a gRPC duplex server.
/// Supports bidirectional request/response with handler registration.
/// Handles both protobuf messages and arbitrary types (via serializer).
/// </summary>
/// <param name="options">Connection options.</param>
/// <param name="serializer">Optional payload serializer for non-protobuf types. Defaults to JSON.</param>
/// <param name="logger">Optional logger.</param>
public sealed class DuplexClient(
    DuplexClientOptions options,
    IPayloadSerializer? serializer = null,
    ILogger<DuplexClient>? logger = null) : IAsyncDisposable
{
    private GrpcChannel? _grpcChannel;
    private DuplexService.DuplexServiceClient? _client;
    private AsyncDuplexStreamingCall<ProtocolDataUnit, ProtocolDataUnit>? _streamingCall;
    private DuplexChannel? _duplexChannel;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveTask;
    private SemaphoreSlim? _writeLock;

    /// <summary>
    /// Gets the underlying duplex channel for registering handlers.
    /// </summary>
    public IDuplexChannel Channel => _duplexChannel
        ?? throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

    /// <summary>
    /// Indicates whether the client is connected.
    /// </summary>
    public bool IsConnected => _duplexChannel?.IsConnected ?? false;

    /// <summary>
    /// Connects to the server.
    /// </summary>
    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_duplexChannel is not null)
        {
            throw new InvalidOperationException("Already connected");
        }

        logger?.LogInformation("Connecting to {Server}", options.ServerAddress);

        var httpHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        _grpcChannel = GrpcChannel.ForAddress(options.ServerAddress, new GrpcChannelOptions
        {
            HttpHandler = httpHandler
        });

        _client = new DuplexService.DuplexServiceClient(_grpcChannel);

        var metadata = new Metadata
        {
            { "x-client-id", options.ClientId }
        };

        if (options.Metadata is not null)
        {
            foreach (var kvp in options.Metadata)
            {
                metadata.Add(kvp.Key, kvp.Value);
            }
        }

        _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _streamingCall = _client.Open(metadata, cancellationToken: _connectionCts.Token);
        _writeLock = new SemaphoreSlim(1, 1);

        // Create the duplex channel with serializer
        var channelId = Guid.NewGuid().ToString("N");
        _duplexChannel = new DuplexChannel(channelId, serializer);

        // Attach the sender
        _duplexChannel.AttachSender(
            async (message, ct) =>
            {
                await _writeLock!.WaitAsync(ct);
                try
                {
                    await _streamingCall.RequestStream.WriteAsync(message, ct);
                }
                finally
                {
                    _writeLock.Release();
                }
            },
            "server",
            includeStackTrace: options.IncludeStackTrace);

        // Start receiving messages
        _receiveTask = ReceiveMessagesAsync(_connectionCts.Token);

        logger?.LogInformation("Connected to {Server} as {ClientId}", options.ServerAddress, options.ClientId);
    }

    /// <summary>
    /// Disconnects from the server.
    /// </summary>
    public async ValueTask DisconnectAsync()
    {
        if (_duplexChannel is null)
        {
            return;
        }

        logger?.LogInformation("Disconnecting from server");

        try
        {
            _connectionCts?.Cancel();

            if (_streamingCall is not null)
            {
                await _streamingCall.RequestStream.CompleteAsync();
            }

            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    logger?.LogWarning("Receive task did not complete in time");
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Error during disconnect");
        }
        finally
        {
            _duplexChannel?.Disconnect("Client disconnected");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();

        if (_duplexChannel is not null)
        {
            await _duplexChannel.DisposeAsync();
            _duplexChannel = null;
        }

        _streamingCall?.Dispose();
        _grpcChannel?.Dispose();
        _connectionCts?.Dispose();
        _writeLock?.Dispose();
    }

    private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _streamingCall!.ResponseStream.ReadAllAsync(cancellationToken))
            {
                await _duplexChannel!.ProcessIncomingAsync(message, cancellationToken);
            }

            // Stream ended normally
            logger?.LogInformation("Server closed the connection");
            _duplexChannel?.Disconnect("Server closed the connection");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            logger?.LogInformation("Connection was cancelled");
            _duplexChannel?.Disconnect("Connection cancelled");
        }
        catch (RpcException ex)
        {
            // Convert gRPC exception to ProblemDetails
            var problem = MapGrpcException(ex);
            logger?.LogError(ex, "gRPC error: {Status} - {Detail}", ex.StatusCode, ex.Status.Detail);
            _duplexChannel?.Fault(problem, ex);
        }
        catch (OperationCanceledException)
        {
            logger?.LogInformation("Receive loop was cancelled");
            _duplexChannel?.Disconnect("Operation cancelled");
        }
        catch (Exception ex)
        {
            // Convert general exception to ProblemDetails
            var problem = ProblemDetails.FromException(ex, options.IncludeStackTrace);
            logger?.LogError(ex, "Error receiving messages");
            _duplexChannel?.Fault(problem, ex);
        }
    }

    /// <summary>
    /// Maps a gRPC exception to ProblemDetails.
    /// </summary>
    private static ProblemDetails MapGrpcException(RpcException ex)
    {
        var grpcStatus = ex.StatusCode.ToString();
        var detail = !string.IsNullOrEmpty(ex.Status.Detail)
            ? ex.Status.Detail
            : ex.Message;

        return ex.StatusCode switch
        {
            StatusCode.Unavailable => ProblemDetails.TransportError(grpcStatus, $"Service unavailable: {detail}"),
            StatusCode.DeadlineExceeded => ProblemDetails.Timeout(0) with { Detail = detail },
            StatusCode.Cancelled => ProblemDetails.Cancelled(),
            StatusCode.Unauthenticated => new ProblemDetails(
                Type: "urn:grpc:duplex:unauthenticated",
                Title: "Unauthenticated",
                Status: (int)Contracts.StatusCode.Unauthorized,
                Detail: detail,
                Code: "GRPC_UNAUTHENTICATED"),
            StatusCode.PermissionDenied => new ProblemDetails(
                Type: "urn:grpc:duplex:permission-denied",
                Title: "Permission Denied",
                Status: (int)Contracts.StatusCode.Unauthorized,
                Detail: detail,
                Code: "GRPC_PERMISSION_DENIED"),
            StatusCode.NotFound => ProblemDetails.NotFound(detail),
            StatusCode.InvalidArgument => ProblemDetails.InvalidMessage(detail),
            StatusCode.Internal => new ProblemDetails(
                Type: "urn:grpc:duplex:internal",
                Title: "Internal Error",
                Status: (int)Contracts.StatusCode.Internal,
                Detail: detail,
                Code: "GRPC_INTERNAL"),
            _ => ProblemDetails.TransportError(grpcStatus, detail)
        };
    }
}

/// <summary>
/// Options for configuring the duplex client.
/// </summary>
/// <param name="ServerAddress">Server address (e.g., "https://localhost:5001").</param>
/// <param name="ClientId">Client identifier.</param>
/// <param name="Metadata">Optional connection metadata.</param>
/// <param name="IncludeStackTrace">Include stack traces in error responses.</param>
public sealed record DuplexClientOptions(
    string ServerAddress,
    string ClientId,
    IReadOnlyDictionary<string, string>? Metadata = null,
    bool IncludeStackTrace = false)
{
    /// <summary>
    /// Creates options for local development.
    /// </summary>
    public static DuplexClientOptions ForLocalDevelopment(string? clientId = null, int port = 5001) =>
        new($"https://localhost:{port}", clientId ?? $"client-{Environment.ProcessId}", IncludeStackTrace: true);
}
