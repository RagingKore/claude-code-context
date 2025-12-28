using System.Collections.Concurrent;
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
/// gRPC command channel implementation.
/// Uses primary constructor for dependency injection.
/// </summary>
/// <param name="options">Connection options.</param>
/// <param name="logger">Optional logger instance.</param>
public sealed class GrpcCommandChannel(
    ChannelConnectionOptions options,
    ILogger<GrpcCommandChannel>? logger = null) : ICommandChannel
{
    private readonly Channel<CommandRequestEnvelope> _incomingRequests = System.Threading.Channels.Channel.CreateUnbounded<CommandRequestEnvelope>();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResponseEnvelope>> _pendingRequests = new();

    private GrpcChannel? _grpcChannel;
    private ChannelService.ChannelServiceClient? _client;
    private AsyncDuplexStreamingCall<CommandRequest, CommandResponse>? _streamingCall;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveTask;
    private bool _isActive;

    public string ChannelId { get; } = Guid.NewGuid().ToString("N");
    public bool IsActive => _isActive;
    public IAsyncEnumerable<CommandRequestEnvelope> IncomingRequests => ReadRequestsAsync();

    /// <summary>
    /// Opens the command channel.
    /// </summary>
    public async ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_isActive)
        {
            logger?.LogWarning("Channel already open");
            return;
        }

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
                { "x-client-id", options.ClientId ?? ChannelId }
            };

            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _streamingCall = _client.ExecuteCommands(metadata, cancellationToken: _connectionCts.Token);

            _receiveTask = ReceiveResponsesAsync(_connectionCts.Token);
            _isActive = true;

            logger?.LogInformation("Command channel opened to {Server}", options.ServerAddress);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to open command channel to {Server}", options.ServerAddress);
            throw;
        }
    }

    public async ValueTask<CommandResponseEnvelope> SendRequestAsync(CommandRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        EnsureActive();

        var tcs = new TaskCompletionSource<CommandResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingRequests.TryAdd(request.RequestId, tcs))
        {
            throw new InvalidOperationException($"Duplicate request ID: {request.RequestId}");
        }

        try
        {
            var protoRequest = MessageConverter.ToProto(request);
            await _streamingCall!.RequestStream.WriteAsync(protoRequest, cancellationToken);
            logger?.LogDebug("Sent command request {RequestId} for {CommandName}", request.RequestId, request.CommandName);

            using var cts = request.TimeoutMs.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;

            if (cts is not null)
            {
                cts.CancelAfter(request.TimeoutMs!.Value);
            }

            var effectiveToken = cts?.Token ?? cancellationToken;

            using var registration = effectiveToken.Register(() =>
            {
                if (_pendingRequests.TryRemove(request.RequestId, out var removed))
                {
                    removed.TrySetCanceled(effectiveToken);
                }
            });

            return await tcs.Task;
        }
        catch (OperationCanceledException) when (request.TimeoutMs.HasValue)
        {
            _pendingRequests.TryRemove(request.RequestId, out _);
            return CommandResponseEnvelope.Timeout(request.RequestId);
        }
        catch
        {
            _pendingRequests.TryRemove(request.RequestId, out _);
            throw;
        }
    }

    public async ValueTask SendResponseAsync(CommandResponseEnvelope response, CancellationToken cancellationToken = default)
    {
        // For client-side, this would be used if the client is also acting as a command server
        // In typical scenarios, only the server sends responses
        logger?.LogWarning("SendResponseAsync called on client - this is typically a server operation");
        await Task.CompletedTask;
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (!_isActive)
        {
            return;
        }

        try
        {
            _connectionCts?.Cancel();

            // Complete all pending requests with cancellation
            foreach (var kvp in _pendingRequests)
            {
                kvp.Value.TrySetCanceled(cancellationToken);
            }
            _pendingRequests.Clear();

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
            logger?.LogWarning(ex, "Error during channel close");
        }
        finally
        {
            _isActive = false;
            logger?.LogInformation("Command channel closed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();

        _streamingCall?.Dispose();
        _grpcChannel?.Dispose();
        _connectionCts?.Dispose();
        _incomingRequests.Writer.Complete();
    }

    private async Task ReceiveResponsesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var response in _streamingCall!.ResponseStream.ReadAllAsync(cancellationToken))
            {
                var envelope = MessageConverter.FromProto(response);
                logger?.LogDebug("Received response for request {RequestId} with status {Status}", envelope.RequestId, envelope.Status);

                if (_pendingRequests.TryRemove(envelope.RequestId, out var tcs))
                {
                    tcs.TrySetResult(envelope);
                }
                else
                {
                    logger?.LogWarning("Received response for unknown request {RequestId}", envelope.RequestId);
                }
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            logger?.LogInformation("Command channel was cancelled");
        }
        catch (OperationCanceledException)
        {
            logger?.LogInformation("Receive loop was cancelled");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error receiving responses");
        }
        finally
        {
            _incomingRequests.Writer.Complete();

            // Cancel all pending requests
            foreach (var kvp in _pendingRequests)
            {
                kvp.Value.TrySetException(new InvalidOperationException("Channel closed"));
            }
        }
    }

    private async IAsyncEnumerable<CommandRequestEnvelope> ReadRequestsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var request in _incomingRequests.Reader.ReadAllAsync(cancellationToken))
        {
            yield return request;
        }
    }

    private void EnsureActive()
    {
        if (!_isActive || _streamingCall is null)
        {
            throw new InvalidOperationException("Command channel is not active");
        }
    }
}
