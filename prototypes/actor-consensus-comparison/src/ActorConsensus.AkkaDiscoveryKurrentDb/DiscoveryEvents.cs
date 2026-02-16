namespace ActorConsensus.AkkaDiscoveryKurrentDb;

/// <summary>
/// Event appended periodically by each node to signal liveness.
/// Written to the shared discovery stream in KurrentDB.
/// </summary>
public sealed record NodeHeartbeat(string NodeId, string Host, int Port, DateTimeOffset Timestamp);

/// <summary>
/// Event appended on graceful shutdown to immediately remove a node from discovery.
/// </summary>
public sealed record NodeLeft(string NodeId, DateTimeOffset Timestamp);
