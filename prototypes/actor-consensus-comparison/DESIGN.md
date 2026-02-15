# Distributed Partition Assignment — Design Options

## Problem Statement

We need **N workers** (geo-distributed, e.g. Paris / Sydney / Virginia) to **self-organize** and divide **stream partitions** among themselves for processing. Workers subscribe to KurrentDB streams and must:

- Agree on who owns which partitions
- Rebalance when workers join, leave, or crash
- Resume from checkpoints stored in KurrentDB

### Hard Constraints

| Constraint | Rationale |
|---|---|
| **Zero server-side coordination** | No coordinator process, no server-side projection doing assignment logic |
| **KurrentDB as data store only** | Checkpoints, assignments, communication — but no coordination code runs there |
| **No lease-based ownership** | Restricts work distribution strategies; thundering herd on lease expiry |
| **Flexible distribution strategies** | Must support weighted, locality-aware, capacity-based, not just round-robin |
| **Geo-distributed workers** | High-latency links (100-300ms RTT), partition tolerance matters |

---

## Option 1: Peer-to-Peer Bully Election + Leader Assignment

**How it works:** Workers discover each other and run the Bully election algorithm. The elected leader (highest ID wins) assigns partitions to all workers. If the leader dies, the remaining workers re-elect and the new leader reassigns.

```
Workers discover each other (seed list / KurrentDB registry stream)
        │
        ▼
  Bully Election (highest-ID wins)
        │
        ▼
  Leader assigns partitions ──► Writes to KurrentDB "assignments" stream
        │
        ▼
  Workers subscribe to their assigned partitions
        │
  Leader dies?  ──► Survivors re-elect ──► New leader reassigns
```

### Assignment Flow

1. Leader reads current worker list + partition list
2. Leader applies distribution strategy (pluggable — round-robin, weighted, locality-aware, etc.)
3. Leader writes `PartitionAssignment` events to a KurrentDB stream
4. Workers subscribe to that stream and pick up their assignments
5. On rebalance, leader writes new assignments — workers react to changes

### Strengths

- **Full strategy control** — the leader can implement any assignment algorithm
- **Simple mental model** — one decider, everyone else follows
- **Well-understood algorithm** — Bully election is textbook distributed systems
- **No external infrastructure** — workers coordinate directly via actor messages
- **KurrentDB as audit log** — assignment history is a stream of events

### Weaknesses

- **Leader is a bottleneck** — all assignment decisions funnel through one node
- **Split-brain risk** — network partition can cause two leaders (needs term/epoch fencing)
- **Election storms** — rapid leader changes under unstable networks
- **Geo-latency** — Bully election rounds are sequential; 300ms RTT means slow elections
- **Custom implementation** — must build and test election + fencing + assignment logic

### Geo Considerations

- Election rounds take O(N) message exchanges at 100-300ms RTT each
- A single election could take 1-3 seconds in a 3-node geo setup
- Leader in Sydney assigning partitions to Virginia adds latency to every rebalance
- Acceptable if rebalancing is rare (worker crashes, deploys)

### What We'd Spike

| Spike | What it proves |
|---|---|
| **Bully election over gRPC** (Proto.Actor) | Proto.Actor's remote transport handles geo-latency election rounds |
| **Bully election over Artery TCP** (Akka.NET) | Akka.NET's remote transport handles geo-latency election rounds |
| **Assignment via KurrentDB stream** | Leader writes assignments, workers subscribe and react |
| **Split-brain fencing with epoch/term** | Two leaders can't both write valid assignments |

---

## Option 2: Raft Consensus with Embedded Leader

**How it works:** Workers form a Raft consensus group. Raft elects a leader with built-in term fencing, log replication, and consistency guarantees. The Raft leader doubles as the partition assignment leader.

```
Workers form Raft group (log replication + leader election)
        │
        ▼
  Raft Leader elected (majority vote, term-fenced)
        │
        ▼
  Leader proposes partition assignment ──► Replicated to majority
        │
        ▼
  Assignment committed ──► Workers apply
        │
  Leader dies?  ──► Raft elects new leader (pre-vote, no disruption)
```

### Assignment Flow

1. Raft leader proposes assignment as a log entry
2. Entry is replicated to majority of workers (2 of 3)
3. Once committed, all workers apply the assignment
4. New assignments are also log entries — full history
5. Assignment state is the result of replaying the log

### Strengths

- **No split-brain by design** — Raft's majority quorum prevents two leaders
- **Term fencing built-in** — stale leaders can't commit new assignments
- **Consistent assignment state** — all workers see the same committed log
- **Pre-vote extension** — prevents election storms from flapping nodes
- **Battle-tested** — etcd, CockroachDB, TiKV all use Raft

### Weaknesses

- **Majority quorum required** — 3 nodes need 2 alive; 1 failure = still works, 2 = stuck
- **More complex implementation** — log replication, snapshotting, membership changes
- **Overkill?** — we don't need replicated state, just agreement on assignment
- **Library dependency** — need a Raft library or implement from scratch
- **Write amplification** — every assignment change is replicated to all nodes

### Geo Considerations

- Raft heartbeats every 150-300ms; with 200ms RTT, need higher election timeouts (2-5s)
- Commit latency = majority RTT (e.g., Paris→Virginia ≈ 80ms, still fast)
- Pre-vote prevents spurious elections from temporary partitions
- Works well geo-distributed if timeouts are tuned correctly

### What We'd Spike

| Spike | What it proves |
|---|---|
| **Raft leader election** (Proto.Actor) | Raft state machine implemented as actors |
| **Raft leader election** (Akka.NET) | Same with Akka's `Become()` for state transitions |
| **Assignment as Raft log entries** | Partition assignments committed via consensus |
| **Geo-tuned timeouts** | Raft works with 100-300ms RTT between nodes |

---

## Option 3: KurrentDB as Consensus Medium (Log-Based Coordination)

**How it works:** No direct peer-to-peer communication. Workers coordinate entirely through KurrentDB streams using optimistic concurrency (expected version). The total ordering of a KurrentDB stream becomes the consensus mechanism.

```
Worker starts ──► Writes "WorkerJoined" to coordination stream
                         │
                         ▼
               KurrentDB orders all writes (total order)
                         │
                         ▼
               All workers subscribe to coordination stream
                         │
                         ▼
               Each worker independently computes assignment
               (same input ──► same output ──► deterministic)
                         │
  Worker dies? ──► Heartbeat timeout ──► Any worker writes "WorkerLeft"
                   (optimistic concurrency prevents duplicates)
```

### Assignment Flow

1. Workers write lifecycle events to a shared `coordination` stream
2. KurrentDB provides total ordering — all workers see events in the same order
3. Each worker independently applies a **deterministic** assignment function to the stream state
4. Since all workers see the same events in the same order, they all compute the same assignment
5. No leader needed — the stream IS the source of truth
6. Heartbeats are also stream events — workers write periodic "I'm alive" events

### Strengths

- **No peer-to-peer communication** — workers only talk to KurrentDB
- **No election algorithm** — no leader, no split-brain, no election storms
- **Deterministic agreement** — same input + same function = same output on every node
- **KurrentDB is already there** — no additional infrastructure
- **Simpler failure model** — if a worker can't reach KurrentDB, it can't process events anyway
- **Natural audit trail** — coordination stream IS the history

### Weaknesses

- **Deterministic strategy only** — assignment function must be pure (same inputs → same outputs). No "ask the leader to decide based on current load" — every worker must be able to compute independently
- **Heartbeat volume** — N workers × heartbeat interval = lots of events in the coordination stream
- **Clock skew** — "is this worker dead?" requires agreeing on time, which is hard across geo
- **KurrentDB as SPOF** — if KurrentDB is down, no coordination (but also no event processing, so maybe acceptable)
- **Consistency window** — workers process events at different speeds; briefly, two workers might compute different assignments. Need idempotent processing or fencing tokens on partition subscriptions
- **Coordination stream contention** — optimistic concurrency retries under high write contention

### Geo Considerations

- Workers can be near different KurrentDB nodes (if KurrentDB is clustered)
- Coordination latency = write-to-read propagation time in KurrentDB cluster
- No peer-to-peer latency concerns — all communication goes through the database
- Heartbeat events need careful TTL/windowing to avoid unbounded stream growth

### What We'd Spike

| Spike | What it proves |
|---|---|
| **Deterministic assignment from stream** | All workers converge on same assignment from same events |
| **Optimistic concurrency coordination** | Workers can write lifecycle events without conflicts |
| **Heartbeat via events** | Dead worker detection via stream-based heartbeats |
| **Consistency window handling** | Workers handle the brief period of divergent assignments |

---

## Option 4: Consistent Hashing Ring (Fully Decentralized)

**How it works:** No leader at all. Workers hash themselves onto a ring. Partitions hash onto the same ring. Each partition is owned by the next worker clockwise on the ring. Workers only need to agree on ring membership.

```
    Partition Ring (example with 3 workers, 8 partitions)

            P1    P2
         W-Paris ───── P3
        /                \
      P0                  W-Sydney
       |                  |
      P7                  P4
        \                /
         W-Virginia ── P5
            P6

  Worker joins/leaves ──► Ring rebalances ──► Only affected partitions move
```

### Assignment Flow

1. Each worker hashes its ID onto a ring (e.g., `hash(worker-id) mod 2^32`)
2. Each partition hashes onto the same ring
3. A partition is owned by the nearest worker clockwise
4. When a worker joins/leaves, only partitions between the departed worker and the next worker move
5. Virtual nodes (multiple hash positions per worker) ensure even distribution

### Strengths

- **Zero coordination** — no leader, no election, no consensus
- **Minimal rebalancing** — worker change only affects neighboring partitions (K/N partitions move)
- **Proven at scale** — DynamoDB, Cassandra, Riak all use this
- **Instant convergence** — no election rounds, no log replication
- **Symmetric** — every worker runs the same algorithm, no special roles

### Weaknesses

- **Fixed strategy** — hash-based assignment only. No weighted, no locality-aware, no capacity-based
- **Membership agreement required** — workers still need to agree on "who is alive" (back to the same problem, just smaller)
- **Hot partitions** — can't rebalance a hot partition to a less-loaded worker without virtual node tricks
- **Virtual node tuning** — too few = uneven distribution; too many = larger membership state
- **No assignment flexibility** — the hash function IS the strategy. Changing strategy = rehashing everything

### Geo Considerations

- Workers only need membership agreement (smaller problem than full assignment)
- Membership can propagate via gossip or KurrentDB stream (same as Option 3)
- Hash ring is computed locally — no cross-network communication for assignment
- Adding locality-awareness requires careful virtual node placement

### What We'd Spike

| Spike | What it proves |
|---|---|
| **Hash ring with virtual nodes** | Even partition distribution across 3 workers |
| **Membership via KurrentDB stream** | Workers agree on ring membership without P2P |
| **Minimal rebalance on failure** | Only affected partitions move when a worker dies |
| **Hot partition handling** | Can we work around the fixed-strategy limitation? |

---

## Option 5: Protocol-Based Assignment (Protocol Actors)

**How it works:** Workers run a protocol where they propose, vote, and commit partition assignments. No single leader — any worker can propose a rebalance, and a majority must agree. Think "2-phase commit for assignments."

```
Worker detects change (new worker, dead worker, imbalance)
        │
        ▼
  Proposer broadcasts Propose(new-assignment, epoch)
        │
        ▼
  Workers vote: Accept / Reject (based on epoch freshness)
        │
        ▼
  Majority accepts? ──► Proposer broadcasts Commit(assignment, epoch)
        │                        │
        No                       ▼
        │               Workers apply new assignment
        ▼
  Proposer backs off (random delay) and retries
```

### Assignment Flow

1. Any worker can detect an imbalance and become a proposer
2. Proposer computes a new assignment using any strategy
3. Proposer broadcasts `Propose(assignment, epoch)` to all workers
4. Workers vote: accept if epoch > their current epoch, reject otherwise
5. If majority accepts, proposer broadcasts `Commit`
6. If rejected, proposer backs off with random delay and retries with new epoch
7. Committed assignments are persisted to KurrentDB

### Strengths

- **No single leader** — any worker can drive rebalancing
- **Flexible strategy** — proposer chooses assignment algorithm
- **Majority quorum** — prevents split-brain (same as Raft but lighter weight)
- **Simple protocol** — just propose/vote/commit (simpler than full Raft)
- **Natural fit for actors** — each phase is a message exchange

### Weaknesses

- **Competing proposals** — two workers proposing simultaneously = conflict resolution needed
- **Livelock risk** — proposals keep colliding if backoff isn't tuned well
- **Two-phase overhead** — every rebalance requires 2 round-trips across geo
- **Partial failures** — what if commit reaches 2 of 3 workers? Need reconciliation
- **Less studied** — not as well-tested as Raft or Bully in literature

### Geo Considerations

- 2 round-trips at 100-300ms RTT = 400-1200ms per rebalance decision
- Acceptable since rebalancing is infrequent
- Competing proposals more likely with high latency (both propose before seeing the other)
- Random backoff must account for RTT variance

### What We'd Spike

| Spike | What it proves |
|---|---|
| **Propose/vote/commit protocol** (Proto.Actor) | Protocol works with actor message passing |
| **Propose/vote/commit protocol** (Akka.NET) | Same protocol, different framework |
| **Competing proposal resolution** | Epoch-based conflict resolution under concurrency |
| **Partial commit recovery** | What happens when commit reaches only some workers |

---

## Comparison Matrix

| Criteria | 1. Bully + Leader | 2. Raft | 3. KurrentDB Log | 4. Hash Ring | 5. Protocol Actors |
|---|---|---|---|---|---|
| **Server coordination** | None | None | None | None | None |
| **Leases** | None | None | None | None | None |
| **Strategy flexibility** | Any (leader decides) | Any (leader proposes) | Deterministic only | Hash-based only | Any (proposer decides) |
| **Split-brain safety** | Needs term fencing | Built-in | N/A (no leader) | N/A (no leader) | Epoch + majority |
| **Geo-latency impact** | Election rounds slow | Heartbeat tuning | Write propagation | Membership propagation | 2 round-trips per rebalance |
| **Complexity** | Medium | High | Low-Medium | Low | Medium |
| **Single point of failure** | Leader (temporary) | Leader (temporary) | KurrentDB | None | None |
| **Implementation effort** | Low | High | Medium | Low | Medium |
| **Rebalance speed** | Leader decides instantly | Commit to majority | Event propagation | Instant (local hash) | 2-phase voting |
| **Actor framework fit** | Natural | Natural | Less natural (DB-centric) | Minimal actor use | Very natural |

---

## Mapping to Spikes

### Current Spikes (Already Built)

| Spike | Option Covered | Framework |
|---|---|---|
| Bully election — single-system | Option 1 (Bully) | Proto.Actor |
| Bully election — single-system | Option 1 (Bully) | Akka.NET |
| Cluster singleton leader | N/A (built-in, not peer-to-peer) | Akka.NET Cluster |

### Proposed Spikes

| Priority | Spike | Options Covered | What it proves |
|---|---|---|---|
| **P0** | Bully election over gRPC remoting (3 processes) | Option 1 | Proto.Actor handles real network election |
| **P0** | Bully election over Akka.Remote (3 processes) | Option 1 | Akka.NET handles real network election |
| **P0** | KurrentDB stream-based coordination | Option 3 | Log-based consensus without P2P |
| **P1** | Partition assignment via KurrentDB stream | Options 1, 3 | Leader (or deterministic) writes assignments, workers subscribe |
| **P1** | Consistent hash ring with membership stream | Option 4 | Fully decentralized, no leader needed |
| **P1** | Propose/vote/commit protocol | Option 5 | Leaderless majority-based assignment |
| **P2** | Raft consensus group (actor-based) | Option 2 | Full Raft as actors — likely overkill but educational |
| **P2** | Split-brain fencing with epochs | Options 1, 5 | Term/epoch fencing prevents stale leaders |
| **P2** | Geo-latency simulation (artificial delays) | All | How each option behaves at 100-300ms RTT |

### Spike Dependency Graph

```
  Current: Bully single-system (done)
      │
      ├──► P0: Bully over real network (Proto.Actor + Akka.NET)
      │         │
      │         └──► P1: Assignment via KurrentDB stream
      │                    │
      │                    └──► P2: Split-brain fencing
      │
      ├──► P0: KurrentDB log-based coordination
      │         │
      │         └──► P1: Deterministic assignment function
      │
      ├──► P1: Consistent hash ring
      │
      └──► P1: Protocol actors (propose/vote/commit)
                │
                └──► P2: Competing proposal resolution
```

---

## Recommendation

**Start with P0 spikes** — they cover the two most promising approaches:

1. **Option 1 (Bully + Leader)** — maximum strategy flexibility, natural actor fit, already partially built
2. **Option 3 (KurrentDB Log)** — simplest architecture, zero P2P, leverages existing infrastructure

If Option 3 proves viable with deterministic strategies, it may be the winner — it's the simplest thing that could work. If you need non-deterministic strategies (load-based, capacity-based), Option 1 or Option 5 become necessary.

Option 4 (Hash Ring) is worth a P1 spike because it's the only truly zero-coordination option, but the fixed strategy is a real limitation.

Option 2 (Raft) is educational but likely overkill — you'd be building a consensus algorithm to solve a problem that simpler approaches handle.
