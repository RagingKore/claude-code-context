namespace ActorConsensus.Contracts;

/// <summary>
/// Common interface for both Proto.Actor and Akka.NET cluster orchestrators.
/// Allows the runner to drive both implementations identically.
/// </summary>
public interface IConsensusCluster : IAsyncDisposable
{
    /// <summary>Name of the actor framework being used.</summary>
    string FrameworkName { get; }

    /// <summary>Starts all nodes and begins the consensus protocol.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Kills the current leader node, triggering re-election.</summary>
    Task KillLeaderAsync(CancellationToken ct = default);

    /// <summary>Returns a snapshot of the current cluster state.</summary>
    Task<ClusterStatus> GetStatusAsync(CancellationToken ct = default);
}
