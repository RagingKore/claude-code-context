namespace ActorConsensus.KurrentDbMembership;

/// <summary>
/// Appended periodically by each node to signal liveness.
/// </summary>
public sealed record NodeHeartbeat(string NodeId, string Host, int Port, bool IsLeader, DateTimeOffset Timestamp);

/// <summary>
/// Appended when a node claims leadership.
/// </summary>
public sealed record LeaderClaimed(string NodeId, long Term, DateTimeOffset Timestamp);

/// <summary>
/// Appended on graceful shutdown to immediately remove a node.
/// </summary>
public sealed record NodeLeft(string NodeId, DateTimeOffset Timestamp);
