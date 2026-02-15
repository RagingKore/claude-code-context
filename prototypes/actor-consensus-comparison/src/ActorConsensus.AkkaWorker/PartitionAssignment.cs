using System.Collections.Immutable;
using Akka.Cluster;

namespace ActorConsensus.AkkaWorker;

/// <summary>
/// Notification delivered to the user callback when partition ownership changes.
/// Every node computes this independently from the same converged membership state.
/// </summary>
public sealed record PartitionChange(
    IReadOnlySet<int> Assigned,
    IReadOnlySet<int> Revoked,
    IReadOnlySet<int> All,
    bool IsOldest,
    int MemberCount
);

/// <summary>
/// Snapshot of a worker's current partition state, returned by <see cref="Worker.GetStateAsync"/>.
/// </summary>
public sealed record PartitionState(
    IReadOnlySet<int> Partitions,
    bool IsOldest,
    int MemberCount,
    int WorkItemsProcessed
);

/// <summary>
/// Pure, deterministic partition assignment function.
/// Given the same member set, every node produces the same assignment map.
/// Members are sorted using <see cref="Member.AgeOrdering"/> (join order), ensuring:
///   - Stable ordering across gossip convergence
///   - Minimal partition movement when members join/leave (only ~1/N partitions shift)
///   - The oldest member (first in age order) is the implicit leader
/// </summary>
internal static class PartitionAssignment
{
    /// <summary>
    /// Computes the set of partition IDs owned by the given member.
    /// Uses round-robin over age-sorted members for even distribution.
    /// </summary>
    public static ImmutableHashSet<int> Compute(
        IEnumerable<Member> members,
        UniqueAddress self,
        int partitionCount,
        string role)
    {
        var workers = members
            .Where(m => m.Status == MemberStatus.Up)
            .Where(m => m.Roles.Contains(role))
            .Order(Member.AgeOrdering)
            .ToList();

        var myIndex = workers.FindIndex(w => w.UniqueAddress == self);
        if (myIndex < 0) return [];

        var builder = ImmutableHashSet.CreateBuilder<int>();
        for (var p = 0; p < partitionCount; p++)
        {
            if (p % workers.Count == myIndex)
                builder.Add(p);
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Returns true if the given member is the oldest Up member with the specified role.
    /// </summary>
    public static bool IsOldest(
        IEnumerable<Member> members,
        UniqueAddress self,
        string role)
    {
        return members
            .Where(m => m.Status == MemberStatus.Up)
            .Where(m => m.Roles.Contains(role))
            .Order(Member.AgeOrdering)
            .FirstOrDefault()?.UniqueAddress == self;
    }
}
