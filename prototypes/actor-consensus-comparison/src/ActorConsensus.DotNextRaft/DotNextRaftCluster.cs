using System.Diagnostics.CodeAnalysis;
using System.Net;
using ActorConsensus.Contracts;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Membership;

[assembly: Experimental("DOTNEXT001")]

namespace ActorConsensus.DotNextRaft;

/// <summary>
/// Orchestrator that creates 3 dotNext Raft cluster nodes using TCP transport
/// with ConsensusOnlyState (no persistent log — pure leader election).
///
/// Key dotNext Raft concepts demonstrated:
///   - <see cref="RaftCluster.TcpConfiguration"/> for binary TCP transport
///   - <see cref="InMemoryClusterConfigurationStorage{TAddress}"/> for static member list
///   - <see cref="RaftCluster.LeaderChanged"/> event for leader tracking
///   - ConsensusOnlyState (default) — no WAL, no replication, just consensus
///   - Native Raft term tracking (no pseudo-term needed)
///
/// Each <see cref="RaftCluster"/> instance runs on a separate TCP port (simulating 3 processes).
/// </summary>
public sealed class DotNextRaftCluster : IConsensusCluster
{
    private readonly ConsensusLog _log;
    private readonly Dictionary<int, RaftCluster> _clusters = [];
    private readonly Dictionary<int, NodeState> _nodeStates = [];
    private readonly HashSet<int> _aliveNodes = [];

    private int? _currentLeaderId;
    private long _currentTerm;
    private readonly Lock _leaderLock = new();

    private const int BasePort = 7771;
    private const int NodeCount = 3;

    public string FrameworkName => "dotNext Raft";

    public DotNextRaftCluster() : this(new ConsensusLog("dotNext Raft")) { }

    public DotNextRaftCluster(ConsensusLog log) => _log = log;

    public async Task StartAsync(CancellationToken ct = default)
    {
        // All member endpoints — every node needs to know the full cluster topology
        var memberEndpoints = Enumerable.Range(0, NodeCount)
            .Select(i => new IPEndPoint(IPAddress.Loopback, BasePort + i))
            .ToArray();

        for (var i = 1; i <= NodeCount; i++)
        {
            var nodeId = i;
            var port = BasePort + i - 1;

            var config = new RaftCluster.TcpConfiguration(new IPEndPoint(IPAddress.Loopback, port))
            {
                LowerElectionTimeout = 150,
                UpperElectionTimeout = 300,
                RequestTimeout = TimeSpan.FromMilliseconds(140),
                ColdStart = false,
                TransmissionBlockSize = 4096,
            };

            // Register all members in the static configuration storage
            var storage = config.UseInMemoryConfigurationStorage();
            var builder = storage.CreateActiveConfigurationBuilder();
            foreach (var endpoint in memberEndpoints)
                builder.Add(endpoint);
            builder.Build();

            var cluster = new RaftCluster(config);

            // Track leader changes via the Raft LeaderChanged event
            cluster.LeaderChanged += (raftCluster, leader) =>
            {
                if (raftCluster is IRaftCluster raft)
                {
                    lock (_leaderLock)
                    {
                        _currentTerm = raft.Term;
                    }
                }

                if (leader is not null)
                {
                    var leaderEndpoint = leader.EndPoint as IPEndPoint;
                    var leaderId = leaderEndpoint is not null
                        ? leaderEndpoint.Port - BasePort + 1
                        : (int?)null;

                    lock (_leaderLock)
                    {
                        _currentLeaderId = leaderId;
                    }

                    _log.Leader(nodeId, $"Leader elected: Node-{leaderId} (term {_currentTerm})");
                }
                else
                {
                    _log.Election(nodeId, "No consensus — leader is null");
                }
            };

            _clusters[i] = cluster;
            _nodeStates[i] = new NodeState(nodeId);
            _aliveNodes.Add(i);

            await cluster.StartAsync(ct);
            _log.Lifecycle(i, $"Raft node started on port {port}");
        }

        // Wait for Raft election to converge
        _log.Info("Waiting for Raft election to converge...");
        await Task.Delay(3000, ct);

        // Start work simulation timers for each node
        foreach (var (nodeId, state) in _nodeStates)
        {
            state.StartWork(_log);
        }
    }

    public async Task KillLeaderAsync(CancellationToken ct = default)
    {
        int? leaderId;
        lock (_leaderLock)
        {
            leaderId = _currentLeaderId;
        }

        if (leaderId is null || !_clusters.TryGetValue(leaderId.Value, out var leaderCluster))
        {
            _log.Info("No leader to kill");
            return;
        }

        _log.Lifecycle(leaderId.Value, "Killing leader node — stopping Raft cluster instance");
        _aliveNodes.Remove(leaderId.Value);
        _nodeStates[leaderId.Value].StopWork();

        await leaderCluster.StopAsync(CancellationToken.None);

        lock (_leaderLock)
        {
            _currentLeaderId = null;
        }

        _log.Lifecycle(leaderId.Value, "Raft node stopped");
    }

    public Task<ClusterStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var nodes = new List<NodeStatus>();

        int? leaderId;
        long term;
        lock (_leaderLock)
        {
            leaderId = _currentLeaderId;
            term = _currentTerm;
        }

        // Also try to get the latest term from any alive cluster
        foreach (var (nodeId, cluster) in _clusters)
        {
            if (_aliveNodes.Contains(nodeId))
            {
                var raftTerm = ((IRaftCluster)cluster).Term;
                if (raftTerm > term)
                {
                    lock (_leaderLock)
                    {
                        _currentTerm = raftTerm;
                        term = raftTerm;
                    }
                }

                // Check if this node thinks it's the leader
                var leader = cluster.Leader;
                if (leader is not null && leaderId is null)
                {
                    var leaderEp = leader.EndPoint as IPEndPoint;
                    if (leaderEp is not null)
                    {
                        var lid = leaderEp.Port - BasePort + 1;
                        lock (_leaderLock)
                        {
                            _currentLeaderId = lid;
                            leaderId = lid;
                        }
                    }
                }
            }
        }

        foreach (var (nodeId, state) in _nodeStates)
        {
            var isAlive = _aliveNodes.Contains(nodeId);
            var isLeader = isAlive && leaderId == nodeId;

            nodes.Add(new NodeStatus(
                nodeId,
                isAlive,
                isLeader,
                state.WorkItemsProcessed,
                isAlive ? DateTimeOffset.UtcNow : null));
        }

        return Task.FromResult(new ClusterStatus(leaderId, term, nodes));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, state) in _nodeStates)
            state.StopWork();

        foreach (var (nodeId, cluster) in _clusters)
        {
            try
            {
                if (_aliveNodes.Contains(nodeId))
                    await cluster.StopAsync(CancellationToken.None);

                cluster.Dispose();
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        _clusters.Clear();
        _nodeStates.Clear();
        _aliveNodes.Clear();
    }

    /// <summary>
    /// Tracks per-node work simulation state with a periodic timer.
    /// </summary>
    private sealed class NodeState(int nodeId)
    {
        private Timer? _workTimer;
        private int _workItemsProcessed;

        public int WorkItemsProcessed => _workItemsProcessed;

        public void StartWork(ConsensusLog log)
        {
            _workTimer = new Timer(_ =>
            {
                var count = Interlocked.Increment(ref _workItemsProcessed);
                if (count % 5 == 0)
                    log.Work(nodeId, $"Processed work item #{count}");
            }, null, TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(800));
        }

        public void StopWork()
        {
            _workTimer?.Dispose();
            _workTimer = null;
        }
    }
}
