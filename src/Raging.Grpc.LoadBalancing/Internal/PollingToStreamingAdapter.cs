using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Adapts a polling topology source to a streaming interface.
/// Implements retry with exponential backoff on transient failures.
/// </summary>
internal sealed class PollingToStreamingAdapter : IStreamingTopologySource {
    readonly IPollingTopologySource _pollingSource;
    readonly TimeSpan _delay;
    readonly ResilienceOptions _resilience;
    readonly ILogger? _logger;

    /// <summary>
    /// Creates a new adapter.
    /// </summary>
    public PollingToStreamingAdapter(
        IPollingTopologySource pollingSource,
        TimeSpan delay,
        ResilienceOptions resilience,
        ILogger? logger = null) {

        _pollingSource = pollingSource;
        _delay = delay;
        _resilience = resilience;
        _logger = logger;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ClusterTopology> SubscribeAsync(
        TopologyContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, cancellationToken);
        var ct = cts.Token;

        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested) {
            ClusterTopology topology;

            try {
                topology = await _pollingSource
                    .GetClusterAsync(context)
                    .ConfigureAwait(false);

                // Success - reset failure count
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                yield break;
            }
            catch (Exception ex) {
                consecutiveFailures++;

                // If we've exceeded max attempts, let the exception propagate
                // so the resolver can try the next seed
                if (consecutiveFailures >= _resilience.MaxDiscoveryAttempts) {
                    _logger?.PollingMaxAttemptsExceeded(consecutiveFailures, ex);
                    throw;
                }

                // Calculate backoff delay
                var backoff = BackoffCalculator.Calculate(
                    consecutiveFailures,
                    _resilience.InitialBackoff,
                    _resilience.MaxBackoff);

                _logger?.PollingFailedRetrying(consecutiveFailures, backoff, ex);

                try {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    yield break;
                }

                continue;
            }

            yield return topology;

            // If delay is zero or negative, only return one snapshot (one-shot mode)
            if (_delay <= TimeSpan.Zero)
                yield break;

            try {
                await Task.Delay(_delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                yield break;
            }
        }
    }

    /// <inheritdoc />
    public int Compare(ClusterNode x, ClusterNode y) => _pollingSource.Compare(x, y);
}
