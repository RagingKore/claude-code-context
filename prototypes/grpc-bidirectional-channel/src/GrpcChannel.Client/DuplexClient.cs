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
/// </summary>
/// <param name="options">Connection options.</param>
/// <param name="logger">Optional logger.</param>
public sealed class DuplexClient(
    DuplexClientOptions options,
    ILogger<DuplexClient>? logger = null) : IAsyncDisposable
{
    private GrpcChannel? _grpcChannel;
    private DuplexService.DuplexServiceClient? _client;
    private AsyncDuplexStreamingCall<DuplexMessage, DuplexMessage>? _streamingCall;
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

        // Create the duplex channel
        var channelId = Guid.NewGuid().ToString("N");
        _duplexChannel = new DuplexChannel(channelId);

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
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            logger?.LogInformation("Connection was cancelled");
        }
        catch (OperationCanceledException)
        {
            logger?.LogInformation("Receive loop was cancelled");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error receiving messages");
            _duplexChannel?.Disconnect(ex.Message);
        }
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
