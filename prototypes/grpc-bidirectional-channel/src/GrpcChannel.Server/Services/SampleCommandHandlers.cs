using System.Text;
using System.Text.Json;
using GrpcChannel.Protocol.Contracts;
using GrpcChannel.Protocol.Messages;

namespace GrpcChannel.Server.Services;

/// <summary>
/// Sample echo command handler.
/// </summary>
/// <param name="logger">Logger instance.</param>
public sealed class EchoCommandHandler(ILogger<EchoCommandHandler> logger) : ICommandHandler
{
    public string CommandName => "echo";

    public ValueTask<CommandResponseEnvelope> HandleAsync(CommandRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        var message = request.Parameters?.GetValueOrDefault("message") ?? "No message provided";
        logger.LogDebug("Echo command received: {Message}", message);

        var response = CommandResponseEnvelope.Success(
            request.RequestId,
            Encoding.UTF8.GetBytes(message),
            new Dictionary<string, string> { ["echoed"] = "true" });

        return ValueTask.FromResult(response);
    }
}

/// <summary>
/// Sample ping command handler.
/// </summary>
public sealed class PingCommandHandler : ICommandHandler
{
    public string CommandName => "ping";

    public ValueTask<CommandResponseEnvelope> HandleAsync(CommandRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        var response = CommandResponseEnvelope.Success(
            request.RequestId,
            Encoding.UTF8.GetBytes("pong"),
            new Dictionary<string, string>
            {
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
            });

        return ValueTask.FromResult(response);
    }
}

/// <summary>
/// Sample status command handler.
/// </summary>
/// <param name="connectionManager">Connection manager instance.</param>
public sealed class StatusCommandHandler(IConnectionManager connectionManager) : ICommandHandler
{
    public string CommandName => "status";

    public ValueTask<CommandResponseEnvelope> HandleAsync(CommandRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        var connections = connectionManager.GetAllConnections();

        var status = new
        {
            ActiveConnections = connections.Count,
            ConnectionIds = connections.Select(c => c.ConnectionId).ToArray(),
            ServerTime = DateTimeOffset.UtcNow.ToString("O"),
            Uptime = Environment.TickCount64
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(status);

        var response = CommandResponseEnvelope.Success(
            request.RequestId,
            json,
            new Dictionary<string, string> { ["contentType"] = "application/json" });

        return ValueTask.FromResult(response);
    }
}

/// <summary>
/// Sample delay command handler for testing timeouts.
/// </summary>
/// <param name="logger">Logger instance.</param>
public sealed class DelayCommandHandler(ILogger<DelayCommandHandler> logger) : ICommandHandler
{
    public string CommandName => "delay";

    public async ValueTask<CommandResponseEnvelope> HandleAsync(CommandRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        var delayMs = int.TryParse(request.Parameters?.GetValueOrDefault("ms"), out var ms) ? ms : 1000;
        logger.LogDebug("Delay command: waiting for {DelayMs}ms", delayMs);

        await Task.Delay(delayMs, cancellationToken);

        return CommandResponseEnvelope.Success(
            request.RequestId,
            Encoding.UTF8.GetBytes($"Delayed for {delayMs}ms"));
    }
}

/// <summary>
/// Sample math command handler.
/// </summary>
public sealed class MathCommandHandler : ICommandHandler
{
    public string CommandName => "math";

    public ValueTask<CommandResponseEnvelope> HandleAsync(CommandRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        var operation = request.Parameters?.GetValueOrDefault("operation") ?? "add";
        var a = double.TryParse(request.Parameters?.GetValueOrDefault("a"), out var aVal) ? aVal : 0;
        var b = double.TryParse(request.Parameters?.GetValueOrDefault("b"), out var bVal) ? bVal : 0;

        double result = operation.ToLowerInvariant() switch
        {
            "add" => a + b,
            "subtract" or "sub" => a - b,
            "multiply" or "mul" => a * b,
            "divide" or "div" when b != 0 => a / b,
            "divide" or "div" => double.NaN,
            _ => double.NaN
        };

        var response = double.IsNaN(result)
            ? CommandResponseEnvelope.Failed(request.RequestId, $"Invalid operation: {operation}")
            : CommandResponseEnvelope.Success(
                request.RequestId,
                Encoding.UTF8.GetBytes(result.ToString()),
                new Dictionary<string, string>
                {
                    ["operation"] = operation,
                    ["a"] = a.ToString(),
                    ["b"] = b.ToString()
                });

        return ValueTask.FromResult(response);
    }
}
