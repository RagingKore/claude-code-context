using System.Runtime.CompilerServices;

namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Adapts a polling topology source to a streaming interface.
/// </summary>
internal sealed class PollingToStreamingAdapter : IStreamingTopologySource {
    readonly IPollingTopologySource _pollingSource;
    readonly TimeSpan _delay;

    /// <summary>
    /// Creates a new adapter.
    /// </summary>
    public PollingToStreamingAdapter(IPollingTopologySource pollingSource, TimeSpan delay) {
        _pollingSource = pollingSource;
        _delay = delay;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ClusterTopology> SubscribeAsync(
        TopologyContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, cancellationToken);
        var ct = cts.Token;

        while (!ct.IsCancellationRequested) {
            ClusterTopology topology;

            try {
                topology = await _pollingSource
                    .GetClusterAsync(context)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                yield break;
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
