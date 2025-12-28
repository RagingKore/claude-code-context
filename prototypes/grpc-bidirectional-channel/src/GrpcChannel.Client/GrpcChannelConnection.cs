using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;
using GrpcChannel.Protocol;
using GrpcChannel.Protocol.Contracts;
using GrpcChannel.Protocol.Messages;
using GrpcChannel.Protocol.Protos;
using Microsoft.Extensions.Logging;

namespace GrpcChannel.Client;

/// <summary>
/// gRPC channel connection implementation.
/// Uses primary constructor for dependency injection.
/// </summary>
/// <param name="options">Connection options.</param>
/// <param name="logger">Optional logger instance.</param>
public sealed class GrpcChannelConnection(
    ChannelConnectionOptions options,
    ILogger<GrpcChannelConnection>? logger = null) : IChannelConnection
{
    private readonly Channel<ChannelEnvelope> _incomingChannel = System.Threading.Channels.Channel.CreateUnbounded<ChannelEnvelope>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    private GrpcChannel? _grpcChannel;
    private ChannelService.ChannelServiceClient? _client;
    private AsyncDuplexStreamingCall<ChannelMessage, ChannelMessage>? _streamingCall;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveTask;
    private ConnectionState _state = ConnectionState.Disconnected;

    public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
    public bool IsConnected => _state == ConnectionState.Connected;
    public IAsyncEnumerable<ChannelEnvelope> IncomingMessages => ReadMessagesAsync();

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

    /// <summary>
    /// Connects to the server.
    /// </summary>
    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state == ConnectionState.Connected)
        {
            logger?.LogWarning("Already connected");
            return;
        }

        SetState(ConnectionState.Connecting);

        try
        {
            var httpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            _grpcChannel = GrpcChannel.ForAddress(options.ServerAddress, new GrpcChannelOptions
            {
                HttpHandler = httpHandler
            });

            _client = new ChannelService.ChannelServiceClient(_grpcChannel);

            var metadata = new Metadata
            {
                { "x-client-id", options.ClientId ?? ConnectionId }
            };

            if (options.Metadata is not null)
            {
                foreach (var kvp in options.Metadata)
                {
                    metadata.Add(kvp.Key, kvp.Value);
                }
            }

            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _streamingCall = _client.Connect(metadata, cancellationToken: _connectionCts.Token);

            // Start receiving messages
            _receiveTask = ReceiveMessagesAsync(_connectionCts.Token);

            SetState(ConnectionState.Connected);
            logger?.LogInformation("Connected to {Server}", options.ServerAddress);
        }
        catch (Exception ex)
        {
            SetState(ConnectionState.Failed, ex.Message);
            logger?.LogError(ex, "Failed to connect to {Server}", options.ServerAddress);
            throw;
        }
    }

    public async ValueTask SendAsync(ChannelEnvelope envelope, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var message = MessageConverter.ToProto(envelope);
        await _streamingCall!.RequestStream.WriteAsync(message, cancellationToken);
        logger?.LogDebug("Sent message {MessageId}", envelope.Id);
    }

    public ValueTask SendTextAsync(string content, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var envelope = ChannelEnvelope.Create(
            new TextMessagePayload(content),
            correlationId: correlationId,
            senderId: options.ClientId ?? ConnectionId);

        return SendAsync(envelope, cancellationToken);
    }

    public ValueTask SendJsonAsync(string jsonData, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var envelope = ChannelEnvelope.Create(
            new JsonMessagePayload(jsonData),
            correlationId: correlationId,
            senderId: options.ClientId ?? ConnectionId);

        return SendAsync(envelope, cancellationToken);
    }

    public ValueTask SendBinaryAsync(byte[] data, string contentType, string? fileName = null, CancellationToken cancellationToken = default)
    {
        var envelope = ChannelEnvelope.Create(
            new BinaryMessagePayload(data, contentType, fileName),
            senderId: options.ClientId ?? ConnectionId);

        return SendAsync(envelope, cancellationToken);
    }

    public ValueTask SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        var envelope = ChannelEnvelope.Create(
            HeartbeatPayload.Instance,
            senderId: options.ClientId ?? ConnectionId);

        return SendAsync(envelope, cancellationToken);
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state == ConnectionState.Disconnected)
        {
            return;
        }

        try
        {
            _connectionCts?.Cancel();

            if (_streamingCall is not null)
            {
                await _streamingCall.RequestStream.CompleteAsync();
            }

            if (_receiveTask is not null)
            {
                await _receiveTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Error during disconnect");
        }
        finally
        {
            SetState(ConnectionState.Disconnected);
            logger?.LogInformation("Disconnected from server");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();

        _streamingCall?.Dispose();
        _grpcChannel?.Dispose();
        _connectionCts?.Dispose();
        _incomingChannel.Writer.Complete();
    }

    private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _streamingCall!.ResponseStream.ReadAllAsync(cancellationToken))
            {
                var envelope = MessageConverter.FromProto(message);
                logger?.LogDebug("Received message {MessageId} of type {Type}", envelope.Id, message.Type);

                await _incomingChannel.Writer.WriteAsync(envelope, cancellationToken);
                MessageReceived?.Invoke(this, new MessageReceivedEventArgs(envelope));
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
            SetState(ConnectionState.Failed, ex.Message);
        }
        finally
        {
            _incomingChannel.Writer.Complete();
        }
    }

    private async IAsyncEnumerable<ChannelEnvelope> ReadMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var envelope in _incomingChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return envelope;
        }
    }

    private void SetState(ConnectionState newState, string? reason = null)
    {
        var previousState = _state;
        _state = newState;

        if (previousState != newState)
        {
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(previousState, newState, reason));
        }
    }

    private void EnsureConnected()
    {
        if (_state != ConnectionState.Connected || _streamingCall is null)
        {
            throw new InvalidOperationException("Not connected to server");
        }
    }
}
