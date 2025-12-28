using System.Collections.Concurrent;
using System.Diagnostics;
using GrpcChannel.Protocol.Contracts;
using GrpcChannel.Protocol.Messages;

namespace GrpcChannel.Server.Services;

/// <summary>
/// Default implementation of command executor.
/// Uses primary constructor for dependency injection.
/// </summary>
/// <param name="logger">Logger instance.</param>
/// <param name="handlers">Registered command handlers.</param>
public sealed class CommandExecutorService(
    ILogger<CommandExecutorService> logger,
    IEnumerable<ICommandHandler> handlers) : ICommandExecutor
{
    private readonly ConcurrentDictionary<string, ICommandHandler> _handlers =
        new(handlers.ToDictionary(h => h.CommandName, h => h, StringComparer.OrdinalIgnoreCase));

    public IReadOnlyCollection<string> RegisteredCommands => _handlers.Keys.ToList().AsReadOnly();

    public void RegisterHandler(ICommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (_handlers.TryAdd(handler.CommandName, handler))
        {
            logger.LogInformation("Registered command handler for '{CommandName}'", handler.CommandName);
        }
        else
        {
            logger.LogWarning("Command handler for '{CommandName}' already exists", handler.CommandName);
        }
    }

    public bool UnregisterHandler(string commandName)
    {
        if (_handlers.TryRemove(commandName, out _))
        {
            logger.LogInformation("Unregistered command handler for '{CommandName}'", commandName);
            return true;
        }
        return false;
    }

    public async ValueTask<CommandResponseEnvelope> ExecuteAsync(CommandRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();

        logger.LogDebug("Executing command '{CommandName}' with request ID {RequestId}", request.CommandName, request.RequestId);

        if (!_handlers.TryGetValue(request.CommandName, out var handler))
        {
            logger.LogWarning("Command '{CommandName}' not found", request.CommandName);
            return CommandResponseEnvelope.NotFound(request.RequestId, request.CommandName);
        }

        try
        {
            using var cts = request.TimeoutMs.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;

            if (cts is not null)
            {
                cts.CancelAfter(request.TimeoutMs!.Value);
            }

            var effectiveToken = cts?.Token ?? cancellationToken;
            var response = await handler.HandleAsync(request, effectiveToken);

            stopwatch.Stop();
            logger.LogDebug(
                "Command '{CommandName}' completed with status {Status} in {Duration}ms",
                request.CommandName,
                response.Status,
                stopwatch.ElapsedMilliseconds);

            return response with { DurationMs = stopwatch.ElapsedMilliseconds };
        }
        catch (OperationCanceledException) when (request.TimeoutMs.HasValue)
        {
            stopwatch.Stop();
            logger.LogWarning("Command '{CommandName}' timed out after {Timeout}ms", request.CommandName, request.TimeoutMs);
            return CommandResponseEnvelope.Timeout(request.RequestId, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            logger.LogInformation("Command '{CommandName}' was cancelled", request.CommandName);
            return CommandResponseEnvelope.Cancelled(request.RequestId, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "Command '{CommandName}' failed with error", request.CommandName);
            return CommandResponseEnvelope.Failed(request.RequestId, ex.Message, durationMs: stopwatch.ElapsedMilliseconds);
        }
    }
}
