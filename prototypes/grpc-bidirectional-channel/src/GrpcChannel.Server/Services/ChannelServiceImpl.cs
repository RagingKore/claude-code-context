using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Grpc.Core;
using GrpcChannel.Protocol;
using GrpcChannel.Protocol.Contracts;
using GrpcChannel.Protocol.Messages;
using GrpcChannel.Protocol.Protos;

namespace GrpcChannel.Server.Services;

/// <summary>
/// gRPC service implementation for bidirectional channel communication.
/// Uses primary constructor for dependency injection.
/// </summary>
/// <param name="logger">Logger instance.</param>
/// <param name="connectionManager">Connection manager instance.</param>
/// <param name="commandExecutor">Command executor instance.</param>
public sealed class ChannelServiceImpl(
    ILogger<ChannelServiceImpl> logger,
    IConnectionManager connectionManager,
    ICommandExecutor commandExecutor) : ChannelService.ChannelServiceBase
{
    /// <summary>
    /// Handles bidirectional streaming connections.
    /// </summary>
    public override async Task Connect(
        IAsyncStreamReader<ChannelMessage> requestStream,
        IServerStreamWriter<ChannelMessage> responseStream,
        ServerCallContext context)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        var clientId = context.RequestHeaders.GetValue("x-client-id") ?? connectionId;

        logger.LogInformation("Client {ClientId} connected with connection {ConnectionId}", clientId, connectionId);

        var connection = new ServerConnection(connectionId, clientId, responseStream, context.CancellationToken);
        connectionManager.AddConnection(connection);

        try
        {
            // Send connection confirmation
            var connectedMessage = ChannelEnvelope.Create(
                new SystemEventPayload(Messages.SystemEventType.Connected, "Connected successfully"),
                senderId: "server",
                targetId: clientId);

            await connection.SendAsync(connectedMessage);

            // Process incoming messages
            await foreach (var message in requestStream.ReadAllAsync(context.CancellationToken))
            {
                await ProcessMessageAsync(connection, message, context.CancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Connection {ConnectionId} was cancelled", connectionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing connection {ConnectionId}", connectionId);
        }
        finally
        {
            connectionManager.RemoveConnection(connectionId);
            logger.LogInformation("Client {ClientId} disconnected from connection {ConnectionId}", clientId, connectionId);
        }
    }

    /// <summary>
    /// Handles bidirectional command execution.
    /// </summary>
    public override async Task ExecuteCommands(
        IAsyncStreamReader<CommandRequest> requestStream,
        IServerStreamWriter<CommandResponse> responseStream,
        ServerCallContext context)
    {
        var channelId = Guid.NewGuid().ToString("N");
        logger.LogInformation("Command channel {ChannelId} opened", channelId);

        try
        {
            await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
            {
                var envelope = MessageConverter.FromProto(request);
                logger.LogDebug("Received command {CommandName} with request {RequestId}", envelope.CommandName, envelope.RequestId);

                var responseEnvelope = await commandExecutor.ExecuteAsync(envelope, context.CancellationToken);
                var response = MessageConverter.ToProto(responseEnvelope);

                await responseStream.WriteAsync(response, context.CancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Command channel {ChannelId} was cancelled", channelId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing command channel {ChannelId}", channelId);
        }
        finally
        {
            logger.LogInformation("Command channel {ChannelId} closed", channelId);
        }
    }

    private async ValueTask ProcessMessageAsync(ServerConnection connection, ChannelMessage message, CancellationToken cancellationToken)
    {
        var envelope = MessageConverter.FromProto(message);
        logger.LogDebug("Processing message {MessageId} of type {Type}", envelope.Id, message.Type);

        switch (envelope.Payload)
        {
            case HeartbeatPayload:
                // Respond with pong
                var pong = ChannelEnvelope.Create(
                    new SystemEventPayload(Messages.SystemEventType.Pong, "pong"),
                    correlationId: envelope.Id,
                    senderId: "server",
                    targetId: connection.ClientId);
                await connection.SendAsync(pong, cancellationToken);
                break;

            case TextMessagePayload text:
                logger.LogInformation("Received text from {ClientId}: {Content}", connection.ClientId, text.Content);
                // Echo back for demonstration
                var echo = envelope.CreateReply(new TextMessagePayload($"Echo: {text.Content}"));
                await connection.SendAsync(echo, cancellationToken);
                break;

            case SystemEventPayload system when system.EventType == Messages.SystemEventType.Ping:
                var pongReply = envelope.CreateReply(new SystemEventPayload(Messages.SystemEventType.Pong, "pong"));
                await connection.SendAsync(pongReply, cancellationToken);
                break;

            default:
                // Broadcast to other connections
                await connectionManager.BroadcastAsync(envelope, connection.ConnectionId, cancellationToken);
                break;
        }
    }
}

/// <summary>
/// Represents a server-side connection.
/// </summary>
/// <param name="ConnectionId">Unique connection identifier.</param>
/// <param name="ClientId">Client identifier.</param>
/// <param name="ResponseStream">The response stream writer.</param>
/// <param name="CancellationToken">Connection cancellation token.</param>
public sealed record ServerConnection(
    string ConnectionId,
    string ClientId,
    IServerStreamWriter<ChannelMessage> ResponseStream,
    CancellationToken CancellationToken)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Sends a message through the connection.
    /// </summary>
    public async ValueTask SendAsync(ChannelEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var message = MessageConverter.ToProto(envelope);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await ResponseStream.WriteAsync(message, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

/// <summary>
/// Manages active connections.
/// </summary>
public interface IConnectionManager
{
    /// <summary>
    /// Adds a connection to the manager.
    /// </summary>
    void AddConnection(ServerConnection connection);

    /// <summary>
    /// Removes a connection from the manager.
    /// </summary>
    void RemoveConnection(string connectionId);

    /// <summary>
    /// Gets a connection by ID.
    /// </summary>
    ServerConnection? GetConnection(string connectionId);

    /// <summary>
    /// Gets all active connections.
    /// </summary>
    IReadOnlyCollection<ServerConnection> GetAllConnections();

    /// <summary>
    /// Broadcasts a message to all connections except the sender.
    /// </summary>
    ValueTask BroadcastAsync(ChannelEnvelope envelope, string? excludeConnectionId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to a specific connection.
    /// </summary>
    ValueTask SendToAsync(string connectionId, ChannelEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of connection manager.
/// </summary>
/// <param name="logger">Logger instance.</param>
public sealed class ConnectionManager(ILogger<ConnectionManager> logger) : IConnectionManager
{
    private readonly ConcurrentDictionary<string, ServerConnection> _connections = new();

    public void AddConnection(ServerConnection connection)
    {
        _connections[connection.ConnectionId] = connection;
        logger.LogDebug("Added connection {ConnectionId}. Total connections: {Count}", connection.ConnectionId, _connections.Count);
    }

    public void RemoveConnection(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out _))
        {
            logger.LogDebug("Removed connection {ConnectionId}. Total connections: {Count}", connectionId, _connections.Count);
        }
    }

    public ServerConnection? GetConnection(string connectionId) =>
        _connections.GetValueOrDefault(connectionId);

    public IReadOnlyCollection<ServerConnection> GetAllConnections() =>
        _connections.Values.ToList().AsReadOnly();

    public async ValueTask BroadcastAsync(ChannelEnvelope envelope, string? excludeConnectionId = null, CancellationToken cancellationToken = default)
    {
        var tasks = _connections.Values
            .Where(c => c.ConnectionId != excludeConnectionId)
            .Select(c => SendToConnectionSafeAsync(c, envelope, cancellationToken));

        await Task.WhenAll(tasks);
    }

    public async ValueTask SendToAsync(string connectionId, ChannelEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (_connections.TryGetValue(connectionId, out var connection))
        {
            await SendToConnectionSafeAsync(connection, envelope, cancellationToken);
        }
    }

    private async Task SendToConnectionSafeAsync(ServerConnection connection, ChannelEnvelope envelope, CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendAsync(envelope, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send message to connection {ConnectionId}", connection.ConnectionId);
        }
    }
}
