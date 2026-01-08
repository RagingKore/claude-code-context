using Grpc.Net.Client.Balancer;
using Microsoft.Extensions.Logging;

namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Custom load balancer that manages subchannels and creates priority-aware pickers.
/// </summary>
internal sealed class ClusterLoadBalancer : LoadBalancer {
    static readonly BalancerState FailureState = new(ConnectivityState.TransientFailure, new ClusterPicker([]));

    readonly IChannelControlHelper _controller;
    readonly ILogger _logger;

    readonly List<Subchannel> _subchannels = [];
    readonly object _lock = new();

    bool _disposed;

    /// <summary>
    /// Creates a new cluster load balancer.
    /// </summary>
    /// <param name="controller">The channel control helper.</param>
    /// <param name="logger">Logger instance.</param>
    public ClusterLoadBalancer(IChannelControlHelper controller, ILogger logger) {
        _controller = controller;
        _logger = logger;
    }

    /// <inheritdoc />
    public override void UpdateChannelState(ChannelState state) {
        if (_disposed)
            return;

        if (state.Status.StatusCode != Grpc.Core.StatusCode.OK || state.Addresses is null or { Count: 0 }) {
            _controller.UpdateState(FailureState);
            return;
        }

        lock (_lock) {
            UpdateSubchannels(state.Addresses);
        }
    }

    void UpdateSubchannels(IReadOnlyList<BalancerAddress> addresses) {
        // Build set of current endpoints
        var currentEndpoints = new HashSet<(string Host, int Port)>();
        foreach (var subchannel in _subchannels) {
            var addr = subchannel.CurrentAddress;
            if (addr is not null)
                currentEndpoints.Add((addr.EndPoint.Host, addr.EndPoint.Port));
        }

        // Build set of new endpoints
        var newEndpoints = new HashSet<(string Host, int Port)>();
        foreach (var addr in addresses) {
            newEndpoints.Add((addr.EndPoint.Host, addr.EndPoint.Port));
        }

        // Remove subchannels that are no longer needed
        for (var i = _subchannels.Count - 1; i >= 0; i--) {
            var subchannel = _subchannels[i];
            var addr = subchannel.CurrentAddress;

            if (addr is not null && !newEndpoints.Contains((addr.EndPoint.Host, addr.EndPoint.Port))) {
                _subchannels.RemoveAt(i);
                subchannel.Dispose();
            }
        }

        // Add new subchannels
        foreach (var addr in addresses) {
            var key = (addr.EndPoint.Host, addr.EndPoint.Port);

            if (!currentEndpoints.Contains(key)) {
                var options = new SubchannelOptions([addr]);
                var subchannel = _controller.CreateSubchannel(options);

                subchannel.OnStateChanged(s => OnSubchannelStateChanged(subchannel, s));
                _subchannels.Add(subchannel);

                // Request connection
                subchannel.RequestConnection();
            }
        }

        // Update picker with current ready subchannels
        UpdatePicker();
    }

    void OnSubchannelStateChanged(Subchannel subchannel, SubchannelState state) {
        if (_disposed)
            return;

        lock (_lock) {
            // Reconnect if subchannel becomes idle
            if (state.State == ConnectivityState.Idle) {
                subchannel.RequestConnection();
            }

            UpdatePicker();
        }
    }

    void UpdatePicker() {
        // Get ready subchannels sorted by priority
        var ready = _subchannels
            .Where(s => s.State == ConnectivityState.Ready)
            .OrderBy(GetPriority)
            .ToList();

        var connectivityState = ready.Count > 0
            ? ConnectivityState.Ready
            : _subchannels.Any(s => s.State == ConnectivityState.Connecting)
                ? ConnectivityState.Connecting
                : _subchannels.Count > 0
                    ? ConnectivityState.TransientFailure
                    : ConnectivityState.Idle;

        var picker = new ClusterPicker(ready);

        _logger.PickerUpdated(ready.Count, CountTopTier(ready));

        _controller.UpdateState(new BalancerState(connectivityState, picker));
    }

    static int GetPriority(Subchannel subchannel) {
        if (subchannel.Attributes.TryGetValue(ClusterPicker.PriorityAttributeKey, out var value) && value is int priority)
            return priority;

        return int.MaxValue;
    }

    static int CountTopTier(List<Subchannel> sorted) {
        if (sorted.Count == 0)
            return 0;

        var topPriority = GetPriority(sorted[0]);
        var count = 1;

        for (var i = 1; i < sorted.Count; i++) {
            if (GetPriority(sorted[i]) != topPriority)
                break;
            count++;
        }

        return count;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing) {
        if (_disposed)
            return;

        _disposed = true;

        if (disposing) {
            lock (_lock) {
                foreach (var subchannel in _subchannels) {
                    subchannel.Dispose();
                }

                _subchannels.Clear();
            }
        }

        base.Dispose(disposing);
    }
}
