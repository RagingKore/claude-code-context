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

## Option 6: Rendezvous Hashing / Highest Random Weight (HRW)

**How it works:** A simpler alternative to consistent hashing. For each partition, every worker computes `hash(partition-id, worker-id)`. The worker with the **highest hash value** wins that partition. No ring, no virtual nodes — just a function.

```
  For each partition, all workers compute:

  Partition "orders-3":
    hash("orders-3", "paris")    = 0x8A3F...  ◄── highest → Paris owns it
    hash("orders-3", "sydney")   = 0x2B71...
    hash("orders-3", "virginia") = 0x6C0E...

  Partition "orders-7":
    hash("orders-7", "paris")    = 0x1D44...
    hash("orders-7", "sydney")   = 0x5E92...
    hash("orders-7", "virginia") = 0xF1AB...  ◄── highest → Virginia owns it

  Worker "sydney" dies:
    Only partitions where Sydney had highest hash need to move.
    Next-highest worker picks them up. Other partitions: untouched.
```

### Assignment Flow

1. All workers agree on the member list (via KurrentDB stream, gossip, or seed list)
2. For each partition, every worker independently computes `hash(partition, worker)` for all known workers
3. The worker with the highest hash owns that partition
4. When a worker joins/leaves, each node recomputes only the affected partitions
5. No ring construction, no virtual nodes, no data structure to maintain

### Strengths

- **Simpler than consistent hashing** — no ring, no virtual nodes, just a hash function
- **Minimal disruption** — when a worker leaves, only its partitions move (same property as consistent hashing)
- **Even distribution** — with a good hash function, partitions spread evenly without tuning
- **Zero state** — no ring to maintain; the algorithm IS the state
- **O(N) per partition** — compute N hashes to assign one partition (trivial for small N)
- **Deterministic** — all workers compute the same result independently

### Weaknesses

- **Same strategy limitation as consistent hashing** — hash-based only, no weighted/capacity-based
- **O(N) per lookup** — must hash against all workers for each partition (consistent hashing is O(log N) with a sorted ring). Irrelevant for N < 100
- **Membership agreement still needed** — same sub-problem as every other option
- **No built-in replication** — assigning K replicas requires taking top-K hashes instead of top-1

### Weighted Extension

Unlike consistent hashing, HRW can be extended for weighted assignment:

```
  score(partition, worker) = hash(partition, worker) / -log(worker.weight)

  Worker weights: Paris=0.5, Sydney=0.3, Virginia=0.2
  → Paris gets ~50% of partitions, Sydney ~30%, Virginia ~20%
```

This makes HRW more flexible than basic consistent hashing while keeping the simplicity.

### Geo Considerations

- Same as consistent hashing — membership propagation is the only latency concern
- Computation is purely local
- Weighted variant can encode locality preferences (higher weight = more local partitions)

### What We'd Spike

| Spike | What it proves |
|---|---|
| **HRW assignment with membership stream** | Workers independently converge on same assignment |
| **Weighted HRW** | Flexible distribution without a leader |
| **HRW vs consistent hashing comparison** | Which distributes more evenly for our partition count? |

---

## Option 7: Gossip Protocol + CRDT Membership (Crumble & Converge)

**How it works:** Workers gossip their state to each other using a CRDT (Conflict-free Replicated Data Type) membership set. No leader, no voting — just eventual convergence. Once all workers see the same membership, they compute assignments deterministically.

```
  Worker-Paris starts ──► Gossips {Paris: alive@t1} to random peer
          │
          ▼
  Worker-Sydney receives gossip ──► Merges into its own CRDT
          │                          {Paris: alive@t1, Sydney: alive@t2}
          ▼
  Sydney gossips merged state to Virginia
          │
          ▼
  Eventually all workers have same CRDT state
          │
          ▼
  Each worker computes assignment from CRDT membership
  (deterministic function ──► same result everywhere)

  Paris dies? ──► Heartbeat timeout ──► Peers mark {Paris: suspect@t5}
              ──► After φ-accrual threshold ──► {Paris: down@t6}
              ──► Gossip propagates ──► All recompute assignment
```

This is essentially how **Akka Cluster** works internally — the spike we already have with `AkkaClusterSingleton` uses this under the hood.

### Assignment Flow

1. Each worker maintains a CRDT (LWW-Register map or OR-Set) of member states
2. Workers periodically gossip their CRDT state to random peers
3. CRDT merge is commutative, associative, idempotent — order doesn't matter
4. Once a worker's CRDT has converged, it computes assignments deterministically
5. Failure detection via φ-accrual (probabilistic, latency-adaptive)
6. Assignment function can be anything deterministic: hash ring, round-robin, HRW, etc.

### Strengths

- **No leader, no election** — fully symmetric, every worker is equal
- **Partition tolerant** — workers in different network partitions still have a consistent local view
- **Crumbles gracefully** — partial failures don't block the system; each partition continues independently
- **Framework support** — Akka Cluster implements this out of the box
- **φ-accrual failure detection** — adapts to network latency automatically (no fixed heartbeat timeout to tune)
- **Composable** — membership CRDT + any deterministic assignment strategy

### Weaknesses

- **Eventual consistency** — workers may temporarily disagree on membership during convergence
- **Convergence time** — gossip rounds take O(log N) rounds to propagate, each round = RTT
- **False positives** — φ-accrual can incorrectly mark slow nodes as down
- **Complexity under the hood** — CRDTs and gossip are conceptually simple but implementation is subtle
- **Deterministic strategy only** — same limitation as Options 3 and 4 once membership converges
- **State growth** — CRDT tombstones and member history can grow without pruning

### Geo Considerations

- Gossip naturally adapts to geo — random peer selection spreads across regions
- φ-accrual adjusts thresholds per-peer based on observed RTT (Paris→Sydney gets a longer leash)
- Convergence: 3 nodes, 200ms avg RTT → converges in ~2-3 gossip rounds (≈ 1-2s)
- Network partitions: each side continues with its view, reconciles on heal

### What We'd Spike

| Spike | What it proves |
|---|---|
| **Custom gossip protocol** (Proto.Actor) | Gossip + CRDT membership without Akka Cluster |
| **Akka Cluster membership + custom assignment** | Use Akka's built-in gossip but plug in our own assignment function |
| **φ-accrual failure detector** | Adaptive timeout works across geo-latency ranges |
| **CRDT convergence under partition** | Two network halves reconcile correctly on heal |

---

## Option 8: Work Stealing (Claim & Steal)

> **Constraint violation:** This option uses heartbeat-based claim renewal, which is effectively lease-based ownership. The claim + heartbeat + timeout-to-steal pattern is a lease in all but name. Included for completeness but **conflicts with the "no lease-based ownership" hard constraint**.

**How it works:** No upfront assignment at all. Workers greedily claim partitions from a shared pool. If a worker is overloaded or dies, other workers steal its partitions. Reactive rather than planned.

```
  Partition Pool (KurrentDB stream or shared state):
    [P0: unclaimed] [P1: unclaimed] [P2: unclaimed] ... [P7: unclaimed]

  Phase 1 — Claim:
    Paris claims P0, P1, P2    (optimistic concurrency — first writer wins)
    Sydney claims P3, P4, P5
    Virginia claims P6, P7

  Phase 2 — Work:
    Each worker processes its claimed partitions

  Phase 3 — Steal (on imbalance or failure):
    Paris dies ──► P0, P1, P2 become "orphaned" (no heartbeat)
                ──► Sydney steals P0, P1
                ──► Virginia steals P2
```

### Assignment Flow

1. Partitions start as unclaimed entries in a KurrentDB stream
2. Workers race to claim partitions via optimistic concurrency writes (`ClaimPartition` events with expected version)
3. First writer wins — other workers retry on a different unclaimed partition
4. Workers heartbeat their claims periodically
5. If a worker stops heartbeating, its partitions become stealable after a timeout
6. Idle or less-loaded workers steal orphaned partitions

### Strengths

- **No upfront coordination** — workers just start grabbing work
- **Self-balancing** — workers naturally spread across available partitions
- **Handles heterogeneous workers** — fast workers claim more, slow workers claim less
- **Simple mental model** — like a thread pool work queue, but distributed
- **Progressive startup** — workers can start processing immediately, don't wait for election/consensus

### Weaknesses

- **Claim contention** — many workers starting simultaneously = lots of optimistic concurrency retries
- **Thundering herd on failure** — when a worker dies, all surviving workers may race to steal the same partitions
- **No global view** — no single place that knows the current complete assignment
- **Heartbeat overhead** — every worker heartbeats every claimed partition
- **Steal protocol complexity** — "is this partition really orphaned or just slow?" requires careful timeout tuning
- **Uneven initial distribution** — without coordination, fast-starting workers hoard partitions

### Geo Considerations

- Workers near KurrentDB have an advantage in claim races (lower latency = wins more)
- This is actually a feature for locality — workers naturally claim partitions from their nearest KurrentDB node
- Steal timeout must account for geo-latency (a Paris→Sydney heartbeat taking 300ms shouldn't trigger a steal)
- Works well when you WANT locality-biased assignment

### What We'd Spike

| Spike | What it proves |
|---|---|
| **Claim race via optimistic concurrency** | Workers can claim without coordination |
| **Steal protocol with heartbeat timeout** | Orphaned partitions are reclaimed correctly |
| **Contention under simultaneous startup** | 3 workers starting at once don't thrash |
| **Locality-biased claiming** | Workers naturally prefer nearby partitions |

---

## Comparison Matrix

| Criteria | 1. Bully | 2. Raft | 3. KurrentDB Log | 4. Hash Ring | 5. Protocol Actors | 6. Rendezvous (HRW) | 7. Gossip+CRDT | 8. Work Stealing |
|---|---|---|---|---|---|---|---|---|
| **Server coordination** | None | None | None | None | None | None | None | None |
| **Leases** | None | None | None | None | None | None | None | **Yes (hidden)** |
| **Strategy flexibility** | Any | Any | Deterministic | Hash only | Any | Hash + weighted | Deterministic | Capacity-biased |
| **Split-brain safety** | Term fencing | Built-in | N/A (no leader) | N/A | Epoch+majority | N/A | Crumbles safely | N/A (claim-based) |
| **Geo-latency impact** | Election slow | Heartbeat tuning | Write propagation | Membership prop. | 2 round-trips | Membership prop. | Gossip rounds | Claim race latency |
| **Complexity** | Medium | High | Low-Medium | Low | Medium | Low | Medium-High | Medium |
| **Single point of failure** | Leader (temp) | Leader (temp) | KurrentDB | None | None | None | None | KurrentDB |
| **Implementation effort** | Low | High | Medium | Low | Low | Low | High (or use Akka) | Medium |
| **Rebalance speed** | Instant (leader) | Majority commit | Event propagation | Instant (local) | 2-phase voting | Instant (local) | Gossip convergence | Steal timeout |
| **Actor framework fit** | Natural | Natural | DB-centric | Minimal | Very natural | Minimal | Built into Akka | Moderate |
| **Membership sub-problem** | Election | Raft log | KurrentDB stream | Needs solving | Part of protocol | Needs solving | Gossip (built-in) | KurrentDB heartbeats |

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
| **P1** | Rendezvous hashing with weighted distribution | Option 6 | Simpler than hash ring, supports weights |
| **P1** | Propose/vote/commit protocol | Option 5 | Leaderless majority-based assignment |
| **P1** | Akka Cluster gossip + custom assignment | Option 7 | Leverage Akka's CRDT membership with our assignment logic |
| **P2** | Work stealing via KurrentDB claims | Option 8 | Reactive assignment, locality-biased. **Note: violates no-lease constraint** |
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
      │         ├──► P1: Deterministic assignment function
      │         │
      │         └──► P2: Work stealing (claim-based variant) ⚠️ lease-like
      │
      ├──► P1: Consistent hash ring
      │         │
      │         └── compare ──► P1: Rendezvous hashing (HRW)
      │                          (both solve the same problem, pick the better fit)
      │
      ├──► P1: Akka Cluster gossip + custom assignment
      │         (leverages existing AkkaCluster spike)
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

**For the hashing family** (Options 4 and 6), spike them together and compare. Rendezvous hashing (Option 6) is likely the better pick — it's simpler, has no virtual node tuning, and supports weighted distribution. See the [Consistent Hashing Deep Dive](#appendix-a-consistent-hashing-deep-dive) below for a thorough comparison.

Option 7 (Gossip+CRDT) is interesting because we already have the Akka Cluster spike — we could plug in a custom assignment function on top of Akka's gossip membership without building gossip from scratch.

Option 8 (Work Stealing) has a unique locality-biased property, but be aware: claim + heartbeat + timeout-to-steal **is lease-based ownership in disguise**, which conflicts with the no-lease hard constraint. Worth knowing about, but goes against a stated design goal.

Option 2 (Raft) is educational but likely overkill — you'd be building a consensus algorithm to solve a problem that simpler approaches handle.

---
---

## Appendix A: Consistent Hashing Deep Dive

### The Problem It Was Invented to Solve

Consistent hashing was introduced by Karger et al. in 1997 to solve a specific problem: **how do you distribute cache keys across N servers so that adding or removing a server doesn't invalidate all your caches?**

With naive hashing (`key % N`), changing N means almost every key maps to a different server. If you go from 3 to 4 servers, ~75% of keys move. For a cache, that means 75% cache miss rate after adding one server — effectively a cold restart.

Consistent hashing guarantees that when a server is added or removed, only **K/N** keys need to move (K = total keys, N = total servers). Add a 4th server to 3? Only ~25% of keys move.

### How the Ring Works

Imagine a circle (ring) with positions 0 to 2^32-1 (the output range of a 32-bit hash):

```
                    0
                    │
            ┌───────┼───────┐
           /        │        \
          /         │         \
    3/4 · 2^32      │      1/4 · 2^32
         |          │          |
          \         │         /
           \        │        /
            └───────┼───────┘
                    │
                1/2 · 2^32
```

**Step 1 — Place workers on the ring:**

Each worker hashes its ID to a position on the ring:

```
  hash("paris")    = 0x1A00_0000  (≈ position 436M)
  hash("sydney")   = 0x7B00_0000  (≈ position 2068M)
  hash("virginia") = 0xC500_0000  (≈ position 3305M)

                     0
                     │
                Paris (0x1A)
                /              \
              /                  \
  Virginia (0xC5)          Sydney (0x7B)
              \                  /
                \              /
                 ──────────────
```

**Step 2 — Place partitions on the ring:**

Each partition hashes onto the same ring:

```
  hash("partition-0") = 0x0F00_0000
  hash("partition-1") = 0x3200_0000
  hash("partition-2") = 0x5500_0000
  hash("partition-3") = 0x8800_0000
  hash("partition-4") = 0xA100_0000
  hash("partition-5") = 0xD900_0000
  hash("partition-6") = 0xE200_0000
  hash("partition-7") = 0xF500_0000
```

**Step 3 — Assignment rule:**

Each partition is assigned to the **next worker clockwise** on the ring:

```
                        0
                        │
                   P0 ──┤
                  Paris (0x1A)
               /    │  P1        \
             /      │              \
  P7 ─ P6 ─ P5     │          P2    Sydney (0x7B)
  Virginia (0xC5)   │             P3 ─ P4
             \      │              /
               \    │            /
                 ──────────────

  Assignments:
    Paris:    P0, P1          (between Virginia and Paris on the ring)
    Sydney:   P2, P3, P4      (between Paris and Sydney)
    Virginia: P5, P6, P7      (between Sydney and Virginia)
```

**Step 4 — Worker leaves (Sydney crashes):**

Only Sydney's partitions need reassignment. They go to the next worker clockwise after Sydney's position, which is Virginia:

```
  Before:                          After Sydney dies:
    Paris:    P0, P1                 Paris:    P0, P1         (unchanged)
    Sydney:   P2, P3, P4   ──►      Virginia: P2, P3, P4, P5, P6, P7
    Virginia: P5, P6, P7

  Only P2, P3, P4 moved. P0, P1, P5, P6, P7 stayed put.
```

**Step 5 — New worker joins (Tokyo):**

```
  hash("tokyo") = 0x9500_0000  (between P4 and P5 on the ring)

  Before:                          After Tokyo joins:
    Paris:    P0, P1                 Paris:    P0, P1         (unchanged)
    Sydney:   P2, P3, P4            Sydney:   P2, P3         (lost P4)
    Virginia: P5, P6, P7            Tokyo:    P4              (got P4 from Sydney)
                                     Virginia: P5, P6, P7     (unchanged)

  Only P4 moved. Everything else stayed.
```

### The Virtual Node Problem

With only 3 workers, the ring is unevenly divided. One worker might own 50% of the ring, another 15%. This is because hash functions don't guarantee even spacing with only 3 points.

**Solution: Virtual nodes.** Each worker places **multiple points** on the ring:

```
  Instead of:
    hash("paris") → 1 position

  Use:
    hash("paris-vn0") → position A
    hash("paris-vn1") → position B
    hash("paris-vn2") → position C
    ...
    hash("paris-vn149") → position Z

  With 150 virtual nodes per worker × 3 workers = 450 points on the ring.
  Distribution is much more even.
```

**Trade-offs of virtual node count:**

| Virtual Nodes/Worker | Distribution Evenness | Membership State Size | Rebalance Granularity |
|---|---|---|---|
| 1 | Terrible (high variance) | Tiny (3 entries) | Coarse (entire arc moves) |
| 10 | Poor | Small | Moderate |
| 100 | Good (~5% variance) | Medium (300 entries) | Fine |
| 150+ | Excellent (~1-2% variance) | Larger | Very fine |

This is the **main tuning knob** of consistent hashing, and it's the thing that makes rendezvous hashing (Option 6) attractive — HRW needs no virtual nodes and achieves even distribution naturally.

### Implementation in C# (Conceptual)

```csharp
public class ConsistentHashRing<TNode>
{
    private readonly SortedDictionary<uint, TNode> _ring = new();
    private readonly int _virtualNodes;

    public ConsistentHashRing(int virtualNodes = 150)
        => _virtualNodes = virtualNodes;

    public void AddNode(TNode node)
    {
        for (var i = 0; i < _virtualNodes; i++)
        {
            var hash = Hash($"{node}-vn{i}");
            _ring[hash] = node;
        }
    }

    public void RemoveNode(TNode node)
    {
        for (var i = 0; i < _virtualNodes; i++)
        {
            var hash = Hash($"{node}-vn{i}");
            _ring.Remove(hash);
        }
    }

    public TNode GetOwner(string partitionId)
    {
        var hash = Hash(partitionId);

        // Find the first node clockwise from the partition's hash
        foreach (var (nodeHash, node) in _ring)
        {
            if (nodeHash >= hash)
                return node;
        }

        // Wrap around — return the first node on the ring
        return _ring.First().Value;
    }

    private static uint Hash(string key)
        => unchecked((uint)HashCode.Combine(key)); // Use MurmurHash3 or xxHash in practice
}
```

### Real-World Implementations

| System | How They Use It | Virtual Nodes | Notable Twist |
|---|---|---|---|
| **DynamoDB** | Partition data across storage nodes | Yes | Preference lists for replication (N clockwise nodes, not just 1) |
| **Apache Cassandra** | Token ring for partition key routing | Yes (vnodes) | Tokens are explicitly assigned, not hashed |
| **Riak** | Distributed key-value storage | Yes | Ring state gossiped between nodes |
| **Kafka (consumer groups)** | Assign topic partitions to consumers | No | Uses `StickyAssignor` or `CooperativeSticky` — NOT pure consistent hashing, but similar minimal-movement property |
| **Nginx** | Upstream server selection | No | `consistent_hash` directive for cache-friendly load balancing |
| **Memcached clients** | Distribute cache keys across servers | Yes (ketama) | Client-side only — servers don't know about each other |

### Consistent Hashing vs Rendezvous Hashing

Both solve the same core problem (minimal disruption on membership change) but differ in approach:

| Aspect | Consistent Hashing (Ring) | Rendezvous Hashing (HRW) |
|---|---|---|
| **Data structure** | Sorted ring (SortedDictionary / tree) | None (just a function) |
| **Lookup complexity** | O(log V) where V = virtual nodes total | O(N) where N = worker count |
| **Memory** | O(V) for the ring | O(1) — stateless |
| **Virtual nodes needed?** | Yes, for even distribution | No — naturally even |
| **Weighted distribution** | Tricky (vary virtual node count) | Simple (`hash / -log(weight)`) |
| **Add/remove worker** | Update ring: O(V/N) insertions/deletions | Nothing to update — just include/exclude from computation |
| **Implementation** | ~40 lines + tuning | ~10 lines |
| **Replication** | Take next K nodes clockwise | Take top K hash values |
| **Who uses it** | DynamoDB, Cassandra, Riak | GitHub load balancer, Microsoft Azure, Cloudflare |

**For our use case (3-10 workers, 10-100 partitions):** Rendezvous hashing is almost certainly the better choice. The O(N) lookup is irrelevant at our scale, and the simplicity + natural even distribution + weighted support are significant advantages.

Consistent hashing shines at **massive scale** (thousands of nodes, millions of keys) where the O(log V) lookup matters and you can afford the virtual node tuning. For 3 geo-distributed workers with ~50 partitions, it's unnecessary complexity.

### Bounded-Load Consistent Hashing (Google, 2017)

Worth mentioning: Google published a variant called **bounded-load consistent hashing** that addresses hot spots. Each node has a capacity cap (e.g., "no node may hold more than 1.25x average load"). When a node hits its cap, overflow keys go to the next node clockwise.

This is relevant if some partitions are much hotter than others — the bounded-load variant prevents one worker from being overwhelmed. However, rendezvous hashing with weights achieves something similar more naturally.

### Key Takeaway

Consistent hashing is a foundational algorithm worth understanding, but for our partition assignment problem with 3-10 workers, **rendezvous hashing (Option 6) is the practical choice**. It gives the same minimal-disruption guarantee with less code, no tuning, and built-in weight support. Think of consistent hashing as the "industrial-scale" version and rendezvous hashing as the "right-sized" version for our problem.
