using ActorConsensus.Contracts;

namespace ActorConsensus.AkkaWorker;

/// <summary>
/// <see cref="IConsensusCluster"/> adapter that creates 3 partition workers
/// to demonstrate gossip-based partition assignment in the shared runner scenario.
///
/// Unlike the other implementations that elect a leader, this approach has no
/// election at all — every worker independently computes a deterministic partition
/// map from the converged membership. The oldest worker is the "implicit leader."
/// </summary>
public sealed class AkkaWorkerCluster : IConsensusCluster
{
    private readonly ConsensusLog _log;
    private readonly List<Worker> _workers = [];
    private readonly HashSet<int> _aliveNodes = [];

    private long _currentTerm;
    private int? _lastKnownOldestId;

    private const int BasePort = 7661;
    private const string SystemName = "workers";
    private const int PartitionCount = 128;

    public string FrameworkName => "Akka.NET Worker (Gossip Partitions)";

    public AkkaWorkerCluster() : this(new ConsensusLog("AkkaWorker")) { }

    public AkkaWorkerCluster(ConsensusLog log) => _log = log;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var seedNodes = Enumerable.Range(0, 3)
            .Select(i => ("127.0.0.1", BasePort + i))
            .ToArray();

        for (var i = 0; i < 3; i++)
        {
            var nodeId = i + 1;
            var port = BasePort + i;
            var log = _log;

            var worker = Worker.Create(SystemName)
                .ListenOn("127.0.0.1", port)
                .WithSeedNodes(seedNodes)
                .WithPartitionCount(PartitionCount)
                .OnPartitionsChanged(async (change, _) =>
                {
                    if (change.Assigned.Count > 0)
                        log.Work(nodeId, $"Assigned {change.Assigned.Count} partitions (total: {change.All.Count})");
                    if (change.Revoked.Count > 0)
                        log.Work(nodeId, $"Revoked {change.Revoked.Count} partitions (total: {change.All.Count})");
                    if (change.IsOldest)
                        log.Leader(nodeId, "*** Oldest worker (implicit leader) ***");

                    await Task.CompletedTask;
                })
                .Build();

            await worker.StartAsync(ct);
            _workers.Add(worker);
            _aliveNodes.Add(nodeId);

            log.Lifecycle(nodeId, $"Worker started on port {port}");
        }

        _log.Info("Waiting for cluster gossip convergence...");
        await Task.Delay(5000, ct);
    }

    public async Task KillLeaderAsync(CancellationToken ct = default)
    {
        // Find the oldest worker (implicit leader)
        for (var i = 0; i < _workers.Count; i++)
        {
            var nodeId = i + 1;
            if (!_aliveNodes.Contains(nodeId)) continue;

            try
            {
                var state = await _workers[i].GetStateAsync(ct);
                if (!state.IsOldest) continue;

                _log.Lifecycle(nodeId, "Killing oldest worker (implicit leader)");
                _aliveNodes.Remove(nodeId);
                await _workers[i].StopAsync(ct);
                await Task.Delay(3000, ct);
                return;
            }
            catch
            {
                // Worker unreachable, skip
            }
        }
    }

    public async Task<ClusterStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var nodes = new List<NodeStatus>();
        int? currentOldestId = null;

        for (var i = 0; i < _workers.Count; i++)
        {
            var nodeId = i + 1;
            if (!_aliveNodes.Contains(nodeId))
            {
                nodes.Add(new NodeStatus(nodeId, false, false, 0, null));
                continue;
            }

            try
            {
                var state = await _workers[i].GetStateAsync(ct);
                nodes.Add(new NodeStatus(
                    nodeId,
                    IsAlive: true,
                    IsLeader: state.IsOldest,
                    WorkItemsProcessed: state.WorkItemsProcessed,
                    LastHeartbeat: DateTimeOffset.UtcNow));

                if (state.IsOldest)
                    currentOldestId = nodeId;
            }
            catch
            {
                nodes.Add(new NodeStatus(nodeId, false, false, 0, null));
            }
        }

        // Track oldest-worker transitions as pseudo-term
        if (currentOldestId.HasValue && currentOldestId != _lastKnownOldestId)
        {
            _currentTerm++;
            _lastKnownOldestId = currentOldestId;
        }

        return new ClusterStatus(currentOldestId, _currentTerm, nodes);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (worker, i) in _workers.Select((w, i) => (w, i)))
        {
            try
            {
                if (_aliveNodes.Contains(i + 1))
                    await worker.StopAsync();

                await worker.DisposeAsync();
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        _workers.Clear();
        _aliveNodes.Clear();
    }
}
