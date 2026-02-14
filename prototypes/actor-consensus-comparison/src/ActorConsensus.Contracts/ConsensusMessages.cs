namespace ActorConsensus.Contracts;

/// <summary>
/// Shared message types used by both Proto.Actor and Akka.NET implementations.
/// These represent the Bully leader election protocol + work simulation messages.
/// </summary>

// --- Leader Election (Bully Algorithm) ---

/// <summary>Periodic heartbeat from a node to prove liveness.</summary>
public sealed record Heartbeat(int NodeId, bool IsLeader);

/// <summary>Sent by a node that detects the leader is down. Targets all higher-ID nodes.</summary>
public sealed record ElectionCall(int CandidateId, long Term);

/// <summary>Response from a higher-ID node that is alive, suppressing the caller's bid.</summary>
public sealed record ElectionAlive(int ResponderId, long Term);

/// <summary>Broadcast by the winner of an election to all nodes.</summary>
public sealed record LeaderElected(int LeaderId, long Term);

// --- Node Lifecycle ---

/// <summary>Instructs a node actor to begin participating in the cluster.</summary>
public sealed record StartNode;

/// <summary>Instructs a node actor to shut down (simulates crash/failure).</summary>
public sealed record StopNode;

/// <summary>Notification that a specific peer node has been detected as down.</summary>
public sealed record PeerDown(int NodeId);

// --- Work Simulation ---

/// <summary>Internal tick that drives simulated long-running subscription work.</summary>
public sealed record WorkTick;

/// <summary>Represents a unit of work produced by a long-running subscription.</summary>
public sealed record WorkItem(int NodeId, int SequenceNumber, string Payload, DateTimeOffset Timestamp);

// --- Orchestration ---

/// <summary>Request to kill the current leader node.</summary>
public sealed record KillLeader;

/// <summary>Request to get current cluster status.</summary>
public sealed record GetClusterStatus;

/// <summary>Response with current cluster state.</summary>
public sealed record ClusterStatus(
    int? CurrentLeaderId,
    long CurrentTerm,
    IReadOnlyList<NodeStatus> Nodes
);

/// <summary>Status of a single node.</summary>
public sealed record NodeStatus(
    int NodeId,
    bool IsAlive,
    bool IsLeader,
    int WorkItemsProcessed,
    DateTimeOffset? LastHeartbeat
);
