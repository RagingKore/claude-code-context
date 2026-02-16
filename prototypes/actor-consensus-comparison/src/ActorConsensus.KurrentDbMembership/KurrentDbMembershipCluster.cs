using ActorConsensus.Contracts;

namespace ActorConsensus.KurrentDbMembership;

/// <summary>
/// IConsensusCluster implementation backed entirely by KurrentDB membership — no actor framework.
/// Spins up 3 <see cref="MembershipNode"/> instances that discover each other, elect a leader,
/// and simulate work via heartbeat-driven events in a shared KurrentDB stream.
/// </summary>
public sealed class KurrentDbMembershipCluster : IConsensusCluster
{
    private readonly ConsensusLog _log = new("KurrentDB-Membership");
    private readonly List<MembershipNode> _nodes = [];
    private readonly string _connectionString;

    public KurrentDbMembershipCluster(string connectionString = "esdb://localhost:2113?tls=false")
    {
        _connectionString = connectionString;
    }

    public string FrameworkName => "KurrentDB-Membership (no actors)";

    public async Task StartAsync(CancellationToken ct = default)
    {
        _log.Info("Starting 3 membership nodes backed by KurrentDB...");

        for (var i = 1; i <= 3; i++)
        {
            var node = new MembershipNode(new MembershipOptions
            {
                ConnectionString = _connectionString,
                ServiceName = "consensus-spike",
                NodeId = i,
                Host = "localhost",
                Port = 10000 + i,
                HeartbeatInterval = TimeSpan.FromSeconds(1),
                HeartbeatTtl = TimeSpan.FromSeconds(5),
                ElectionDelay = TimeSpan.FromSeconds(2)
            });

            _nodes.Add(node);
            await node.StartAsync();
            _log.Lifecycle(i, "Started");
        }
    }

    public async Task KillLeaderAsync(CancellationToken ct = default)
    {
        var leader = _nodes.FirstOrDefault(n => n.IsLeader && n.IsAlive);

        if (leader is null)
        {
            _log.Info("No active leader to kill.");
            return;
        }

        _log.Lifecycle(leader.NodeId, "Killing leader node...");
        await leader.StopAsync();
        _log.Lifecycle(leader.NodeId, "Node stopped.");
    }

    public Task<ClusterStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var leaderNode = _nodes.FirstOrDefault(n => n.IsLeader && n.IsAlive);

        var nodes = _nodes.Select(n => new NodeStatus(
            n.NodeId,
            n.IsAlive,
            n.IsLeader,
            n.WorkItemsProcessed,
            n.IsAlive ? DateTimeOffset.UtcNow : null
        )).ToList();

        var status = new ClusterStatus(
            leaderNode?.NodeId,
            _nodes.Max(n => n.CurrentTerm),
            nodes
        );

        return Task.FromResult(status);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
            await node.DisposeAsync();

        _nodes.Clear();
    }
}
