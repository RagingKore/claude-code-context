using GrpcChannel.Protocol.Messages;

namespace GrpcChannel.Protocol.Contracts;

/// <summary>
/// Contract for command handlers.
/// </summary>
public interface ICommandHandler
{
    /// <summary>
    /// The name of the command this handler processes.
    /// </summary>
    string CommandName { get; }

    /// <summary>
    /// Handles a command request.
    /// </summary>
    /// <param name="request">The command request envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The command response envelope.</returns>
    ValueTask<CommandResponseEnvelope> HandleAsync(CommandRequestEnvelope request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Generic command handler with typed payload.
/// </summary>
/// <typeparam name="TRequest">Request payload type.</typeparam>
/// <typeparam name="TResponse">Response payload type.</typeparam>
public interface ICommandHandler<TRequest, TResponse> : ICommandHandler
    where TRequest : class
    where TResponse : class;

/// <summary>
/// Contract for command executor that manages multiple command handlers.
/// </summary>
public interface ICommandExecutor
{
    /// <summary>
    /// Registers a command handler.
    /// </summary>
    /// <param name="handler">The command handler to register.</param>
    void RegisterHandler(ICommandHandler handler);

    /// <summary>
    /// Unregisters a command handler.
    /// </summary>
    /// <param name="commandName">The command name to unregister.</param>
    /// <returns>True if the handler was unregistered, false otherwise.</returns>
    bool UnregisterHandler(string commandName);

    /// <summary>
    /// Executes a command request.
    /// </summary>
    /// <param name="request">The command request envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The command response envelope.</returns>
    ValueTask<CommandResponseEnvelope> ExecuteAsync(CommandRequestEnvelope request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all registered command names.
    /// </summary>
    IReadOnlyCollection<string> RegisteredCommands { get; }
}

/// <summary>
/// Contract for bidirectional command channel.
/// </summary>
public interface ICommandChannel : IAsyncDisposable
{
    /// <summary>
    /// Unique channel identifier.
    /// </summary>
    string ChannelId { get; }

    /// <summary>
    /// Indicates whether the channel is active.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Gets an async enumerable of incoming command requests.
    /// </summary>
    IAsyncEnumerable<CommandRequestEnvelope> IncomingRequests { get; }

    /// <summary>
    /// Sends a command request and waits for a response.
    /// </summary>
    /// <param name="request">The command request envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The command response envelope.</returns>
    ValueTask<CommandResponseEnvelope> SendRequestAsync(CommandRequestEnvelope request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a command response.
    /// </summary>
    /// <param name="response">The command response envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SendResponseAsync(CommandResponseEnvelope response, CancellationToken cancellationToken = default);
}
