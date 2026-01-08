using System.Runtime.CompilerServices;

namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Adapts a polling topology source to a streaming interface.
/// </summary>
/// <typeparam name="TNode">The node type.</typeparam>
internal sealed class PollingToStreamingAdapter<TNode> : IStreamingTopologySource<TNode>
    where TNode : struct, IClusterNode {

    readonly IPollingTopologySource<TNode> _pollingSource;
    readonly TimeSpan _delay;

    /// <summary>
    /// Creates a new adapter.
    /// </summary>
    /// <param name="pollingSource">The polling source to adapt.</param>
    /// <param name="delay">The delay between polls.</param>
    public PollingToStreamingAdapter(IPollingTopologySource<TNode> pollingSource, TimeSpan delay) {
        _pollingSource = pollingSource;
        _delay = delay;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ClusterTopology<TNode>> SubscribeAsync(
        TopologyContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {

        var ct = CancellationTokenSource
            .CreateLinkedTokenSource(context.CancellationToken, cancellationToken)
            .Token;

        while (!ct.IsCancellationRequested) {
            ClusterTopology<TNode> topology;

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
    public int Compare(TNode x, TNode y) => _pollingSource.Compare(x, y);
}
