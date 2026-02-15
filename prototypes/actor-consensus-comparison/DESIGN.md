# Distributed Partition Assignment — Design Options

## Problem Statement

We need **N workers** (geo-distributed, e.g. Paris / Sydney / Spain) to **self-organize** and divide **stream partitions** among themselves for processing. Workers subscribe to KurrentDB streams and must:

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

## Scenarios

Every option below is evaluated against the same set of scenarios so they can be compared side by side.

### Setup

```
Workers:     Paris (ID: 1)     Sydney (ID: 2)     Spain (ID: 3)
Partitions:  P0   P1   P2   P3   P4   P5

Desired initial assignment (even split):
  Paris:    P0, P1    (2 partitions)
  Sydney:   P2, P3    (2 partitions)
  Spain: P4, P5    (2 partitions)
```

### Scenario A — Cold Start

All 3 workers start at roughly the same time. They must discover each other and agree on who processes which partitions. No prior state exists.

### Scenario B — Worker Crashes (Sydney Dies)

Sydney crashes unexpectedly. Its partitions (P2, P3) become unprocessed. The surviving workers (Paris, Spain) must detect the failure and reassign P2, P3 so processing continues.

**Key questions:** How fast is detection? How are the orphaned partitions redistributed? Do Paris's existing partitions (P0, P1) stay put?

### Scenario C — Worker Recovers (Sydney Returns)

Sydney comes back after a restart. The system must detect the new worker and rebalance, ideally returning to the original even split.

**Key questions:** Do P2, P3 return to Sydney (sticky)? How much disruption to Paris and Spain?

### Scenario D — Scale Out (Tokyo Joins)

A 4th worker (Tokyo, ID: 4) joins the cluster. 6 partitions across 4 workers means some workers get 2, some get 1.

**Key questions:** How many partitions move? Which workers are disrupted? Does the system converge to a balanced assignment?

---

## Option 1: Peer-to-Peer Bully Election + Leader Assignment

**How it works:** Workers discover each other and run the Bully election algorithm. The elected leader (highest ID wins) assigns partitions to all workers. If the leader dies, the remaining workers re-elect and the new leader reassigns.

### Scenario Walkthrough

**A — Cold Start:**

```
  Paris(1), Sydney(2), Spain(3) start and discover each other
        │
        ▼
  Bully Election: each worker sends ElectionCall to higher-ID workers
    Paris(1) ──► Sydney(2), Spain(3)     Sydney(2) ──► Spain(3)
    Paris(1) gets Alive from Sydney, backs off
    Sydney(2) gets Alive from Spain, backs off
    Spain(3) gets no Alive ──► broadcasts LeaderElected(Spain)
        │
        ▼
  Spain (leader) decides assignment:
    Paris: P0, P1  │  Sydney: P2, P3  │  Spain: P4, P5
  Writes PartitionAssignment to KurrentDB stream
  Paris and Sydney subscribe and pick up their assignments
```

**B — Sydney Crashes:**

```
  Spain (leader) detects Sydney's heartbeat timeout after ~2s
        │
        ▼
  Spain decides new assignment for survivors:
    Paris: P0, P1, P2  │  Spain: P3, P4, P5
  Writes new PartitionAssignment to KurrentDB stream
  Paris picks up P2 from stream and starts processing it
  P0, P1, P4, P5 stay exactly where they were (sticky ✓)
```

No re-election needed — the leader (Spain) is still alive. It just reassigns.

**C — Sydney Returns:**

```
  Spain (leader) detects Sydney's heartbeat ──► new worker!
        │
        ▼
  Spain decides new assignment:
    Paris: P0, P1  │  Sydney: P2, P3  │  Spain: P4, P5
  Writes PartitionAssignment to KurrentDB stream
  Paris releases P2, Sydney picks up P2 and P3
  Back to original assignment (sticky ✓)
```

**D — Tokyo Joins:**

```
  Spain (leader) detects Tokyo's heartbeat ──► new worker!
        │
        ▼
  Spain decides new assignment (6 partitions / 4 workers):
    Paris: P0, P1  │  Sydney: P2  │  Spain: P4  │  Tokyo: P3, P5
  Writes PartitionAssignment to KurrentDB stream
  Sydney releases P3, Spain releases P5, Tokyo picks them up
  P0, P1, P2, P4 stay put (sticky ✓). Only 2 partitions moved.
```

The leader has full control over strategy — it can optimize for stickiness, locality, weights, anything.

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
- Leader in Sydney assigning partitions to Spain adds latency to every rebalance
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

### Scenario Walkthrough

**A — Cold Start:**

```
  Paris(1), Sydney(2), Spain(3) form Raft group
        │
        ▼
  Election: each node starts with random timeout (150-300ms)
  Spain times out first ──► requests votes from Paris, Sydney (term=1)
  Paris votes yes, Sydney votes yes ──► Spain wins (2/3 majority)
        │
        ▼
  Spain (Raft leader, term=1) proposes log entry:
    Assign { Paris: P0,P1 | Sydney: P2,P3 | Spain: P4,P5 }
  Replicated to Paris (ack) + Sydney (ack) ──► 2/3 majority ──► committed
  All 3 workers apply the committed assignment
```

**B — Sydney Crashes:**

```
  Spain (leader) stops receiving Raft heartbeat acks from Sydney
  After election timeout ──► Spain marks Sydney as removed from cluster
        │
        ▼
  Spain proposes log entry (term=1):
    Assign { Paris: P0,P1,P2 | Spain: P3,P4,P5 }
  Replicated to Paris (ack) ──► 2/2 alive = majority ──► committed
  Paris picks up P2. P0, P1, P4, P5 stay put (sticky ✓)
```

Key: Raft quorum is 2 of 3 — system continues with 2 alive. If Paris ALSO died, Spain alone (1/3) couldn't commit and the system would be stuck.

**C — Sydney Returns:**

```
  Sydney restarts ──► joins Raft group ──► receives all committed log entries
  Spain proposes: Assign { Paris: P0,P1 | Sydney: P2,P3 | Spain: P4,P5 }
  Replicated to majority ──► committed ──► all apply
  Back to original (sticky ✓)
```

**D — Tokyo Joins:**

```
  Raft membership change: add Tokyo (2-phase: joint consensus)
  Spain proposes: Assign { Paris: P0,P1 | Sydney: P2 | Spain: P4 | Tokyo: P3,P5 }
  Replicated to majority of NEW config (3/4) ──► committed
  2 partitions moved, 4 stayed (sticky ✓)
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
- Commit latency = majority RTT (e.g., Paris→Spain ≈ 80ms, still fast)
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

### Scenario Walkthrough

**A — Cold Start:**

```
  Paris writes:  { type: "WorkerJoined", worker: "paris" }   ──► stream v0
  Sydney writes: { type: "WorkerJoined", worker: "sydney" }  ──► stream v1
  Spain writes:  { type: "WorkerJoined", worker: "spain" }   ──► stream v2

  KurrentDB guarantees total order: v0, v1, v2

  All 3 workers subscribe to the coordination stream.
  Each independently reads [paris, sydney, spain] and runs:
    deterministic_assign([paris, sydney, spain], [P0..P5])
      → Paris: P0,P1 | Sydney: P2,P3 | Spain: P4,P5

  Same function, same input ──► same output on all 3 workers ✓
```

**B — Sydney Crashes:**

```
  Paris and Spain notice Sydney's heartbeat events stopped
  Paris writes: { type: "WorkerLeft", worker: "sydney" }  ──► stream v47
    (Spain tried to write the same event but gets concurrency conflict — retries
     and sees Paris already wrote it, so it skips)

  All workers re-read stream state: members = [paris, spain]
    deterministic_assign([paris, spain], [P0..P5])
      → Paris: P0,P1,P2 | Spain: P3,P4,P5

  P0, P1, P4, P5 stay put (sticky ✓). P2, P3 redistributed.
```

**C — Sydney Returns:**

```
  Sydney writes: { type: "WorkerJoined", worker: "sydney" }  ──► stream v52

  All workers re-read: members = [paris, spain, sydney]
    deterministic_assign([paris, spain, sydney], [P0..P5])
      → Paris: P0,P1 | Sydney: P2,P3 | Spain: P4,P5

  Back to original (sticky ✓ — if the deterministic function is stable)
```

**D — Tokyo Joins:**

```
  Tokyo writes: { type: "WorkerJoined", worker: "tokyo" }  ──► stream v60

  All workers re-read: members = [paris, sydney, spain, tokyo]
    deterministic_assign([paris, sydney, spain, tokyo], [P0..P5])
      → Paris: P0,P1 | Sydney: P2 | Spain: P4 | Tokyo: P3,P5

  How many partitions move depends entirely on the deterministic function.
  With round-robin: potentially many. With HRW: only 1-2 (sticky ✓).
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

**How it works:** No leader at all. Workers hash themselves onto a ring. Partitions hash onto the same ring. Each partition is owned by the next worker clockwise on the ring. Workers only need to agree on ring membership. (See [Appendix A](#appendix-a-consistent-hashing-deep-dive) for a detailed visual walkthrough.)

### Scenario Walkthrough

Using a simplified ring (0-99). Workers and partitions are hashed:

```
  hash("paris")=15   hash("sydney")=48   hash("spain")=79
  hash("P0")=5  hash("P1")=22  hash("P2")=37  hash("P3")=55  hash("P4")=68  hash("P5")=90
```

**A — Cold Start:**

```
  Workers join the ring at their hash positions. Rule: walk clockwise, first worker owns it.

  P0(5)  → Paris(15) ✓     P1(22) → Sydney(48) ✓     P2(37) → Sydney(48) ✓
  P3(55) → Spain(79) ✓     P4(68) → Spain(79) ✓      P5(90) → Paris(15) ✓ (wraps)

  Result: Paris: P0,P5 | Sydney: P1,P2 | Spain: P3,P4
```

Note: the hash ring assigns by position, not by our desired split. Paris gets P0+P5 (not P0+P1). This is the trade-off — the hash function decides, not you.

**B — Sydney Crashes:**

```
  Remove Sydney(48) from ring. Re-walk clockwise:

  P1(22) → Spain(79) ← was Sydney     P2(37) → Spain(79) ← was Sydney
  Everything else unchanged.

  Result: Paris: P0,P5 (unchanged ✓) | Spain: P1,P2,P3,P4 (got Sydney's)

  Problem: Spain gets 4 partitions, Paris gets 2. Uneven!
  All of Sydney's load went to the next clockwise neighbor.
```

**C — Sydney Returns:**

```
  Re-add Sydney(48). P1 and P2 go back to Sydney.

  Result: Paris: P0,P5 | Sydney: P1,P2 | Spain: P3,P4
  Perfectly sticky ✓ — same hash, same position, same assignment.
```

**D — Tokyo Joins:**

```
  hash("tokyo")=60. Inserted on ring between P3(55) and P4(68).

  P3(55) → Tokyo(60) ← was Spain     Everything else unchanged.

  Result: Paris: P0,P5 | Sydney: P1,P2 | Spain: P4 | Tokyo: P3
  Only 1 partition moved! Minimal disruption ✓
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

### Scenario Walkthrough

**A — Cold Start:**

```
  Paris, Sydney, Spain start. All detect "no current assignment" (epoch=0).
  Spain happens to detect first, becomes proposer:

  Spain ──Propose(epoch=1)──►  Paris: { P0,P1 | P2,P3 | P4,P5 }
                               Sydney: same proposal
        │
  Paris votes Accept(epoch=1)    Sydney votes Accept(epoch=1)
  2/3 majority ──► Spain broadcasts Commit(epoch=1)
  All 3 workers apply assignment.
```

**B — Sydney Crashes:**

```
  Paris and Spain both detect Sydney's heartbeat timeout.
  Both could propose — race condition!

  Paris ──Propose(epoch=2)──► Spain:  { P0,P1,P2 | P3,P4,P5 }
  Spain ──Propose(epoch=2)──► Paris:  { P0,P1,P2 | P3,P4,P5 }  (same epoch!)

  Conflict resolution: lower-ID proposer wins ties at same epoch.
  Paris(1) < Spain(3) ──► Paris's proposal wins
  Spain votes Accept for Paris's proposal, Reject for its own
  Paris broadcasts Commit(epoch=2)

  Result: Paris: P0,P1,P2 | Spain: P3,P4,P5
  P0, P1, P4, P5 stay put (sticky ✓)
```

**C — Sydney Returns:**

```
  Spain detects Sydney's heartbeat ──► proposes epoch=3:
    { Paris: P0,P1 | Sydney: P2,P3 | Spain: P4,P5 }
  Paris and Sydney vote Accept ──► 3/3 ──► Commit
  Back to original (sticky ✓)
```

**D — Tokyo Joins:**

```
  Paris detects Tokyo ──► proposes epoch=4:
    { Paris: P0,P1 | Sydney: P2 | Spain: P4 | Tokyo: P3,P5 }
  Sydney, Spain, Tokyo vote Accept ──► 4/4 ──► Commit
  2 partitions moved, 4 stayed (sticky ✓)
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

### Scenario Walkthrough

**A — Cold Start:**

```
  Workers agree on member list: [paris, sydney, spain]
  For each partition, compute hash(partition, worker) for all workers.
  Highest hash wins:

  P0: hash(P0,paris)=82  hash(P0,sydney)=41  hash(P0,spain)=67   → Paris ✓
  P1: hash(P1,paris)=23  hash(P1,sydney)=71  hash(P1,spain)=55   → Sydney ✓
  P2: hash(P2,paris)=64  hash(P2,sydney)=38  hash(P2,spain)=91   → Spain ✓
  P3: hash(P3,paris)=17  hash(P3,sydney)=88  hash(P3,spain)=44   → Sydney ✓
  P4: hash(P4,paris)=53  hash(P4,sydney)=29  hash(P4,spain)=76   → Spain ✓
  P5: hash(P5,paris)=95  hash(P5,sydney)=60  hash(P5,spain)=12   → Paris ✓

  Result: Paris: P0,P5 | Sydney: P1,P3 | Spain: P2,P4
  Even 2-2-2 split ✓ (hash functions distribute evenly without tuning)
```

Note: like the hash ring, the hash function decides the assignment, not you. But the distribution is naturally even without virtual nodes.

**B — Sydney Crashes:**

```
  Remove Sydney from member list. Recompute only Sydney's partitions:

  P1: was Sydney. Remaining: hash(P1,paris)=23  hash(P1,spain)=55 → Spain ✓
  P3: was Sydney. Remaining: hash(P3,paris)=17  hash(P3,spain)=44 → Spain ✓

  Result: Paris: P0,P5 (unchanged ✓) | Spain: P1,P2,P3,P4 (got Sydney's)

  Only P1, P3 moved. P0, P2, P4, P5 untouched (sticky ✓).
  Same unevenness as hash ring — Sydney's partitions go to whoever
  had the next-highest hash, which could be the same worker.
```

**C — Sydney Returns:**

```
  Add Sydney back to member list. Recompute:

  P1: hash(P1,sydney)=71 is still highest → Sydney ✓
  P3: hash(P3,sydney)=88 is still highest → Sydney ✓

  Result: Paris: P0,P5 | Sydney: P1,P3 | Spain: P2,P4
  Perfectly sticky ✓ — same members = same hashes = same assignment.
```

**D — Tokyo Joins:**

```
  Add Tokyo. Recompute all:

  P0: hash(P0,tokyo)=36 — Paris(82) still highest → Paris ✓ (unchanged)
  P1: hash(P1,tokyo)=84 — higher than Sydney(71)! → Tokyo ✓ (moved from Sydney)
  P2: hash(P2,tokyo)=19 — Spain(91) still highest → Spain ✓ (unchanged)
  P3: hash(P3,tokyo)=52 — Sydney(88) still highest → Sydney ✓ (unchanged)
  P4: hash(P4,tokyo)=80 — higher than Spain(76)! → Tokyo ✓ (moved from Spain)
  P5: hash(P5,tokyo)=33 — Paris(95) still highest → Paris ✓ (unchanged)

  Result: Paris: P0,P5 | Sydney: P3 | Spain: P2 | Tokyo: P1,P4
  Only 2 partitions moved (P1, P4). 4 stayed (sticky ✓).
```

### Assignment Flow

1. All workers agree on the member list (via KurrentDB stream, gossip, or seed list)
2. For each partition, every worker independently computes `hash(partition, worker)` for all workers
3. The worker with the highest hash owns that partition
4. When a worker joins/leaves, each node recomputes — only affected partitions change
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

  Worker weights: Paris=0.5, Sydney=0.3, Spain=0.2
  → Paris gets ~50% of partitions, Sydney ~30%, Spain ~20%
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

This is essentially how **Akka Cluster** works internally — the spike we already have with `AkkaClusterSingleton` uses this under the hood.

### Scenario Walkthrough

**A — Cold Start:**

```
  t=0  Paris starts.     Paris's CRDT: {paris: Up}
  t=0  Sydney starts.    Sydney's CRDT: {sydney: Up}
  t=0  Spain starts.     Spain's CRDT: {spain: Up}

  t=1  Paris gossips to Sydney → Sydney's CRDT: {paris: Up, sydney: Up}
  t=1  Spain gossips to Paris  → Paris's CRDT: {paris: Up, spain: Up}

  t=2  Sydney gossips (merged) to Spain
       → Spain's CRDT: {paris: Up, sydney: Up, spain: Up}
  t=2  Paris gossips to Sydney
       → Sydney's CRDT: {paris: Up, sydney: Up, spain: Up}

  t=3  All CRDTs converged: {paris: Up, sydney: Up, spain: Up}
       Each worker runs: deterministic_assign([paris,sydney,spain], [P0..P5])
         → Paris: P0,P1 | Sydney: P2,P3 | Spain: P4,P5
```

3 gossip rounds at ~200ms RTT each ≈ 600ms to converge.

**B — Sydney Crashes:**

```
  t=10  Paris's φ-accrual detector marks Sydney as suspect (missed heartbeats)
  t=11  Spain's φ-accrual detector also marks Sydney as suspect
  t=12  Both independently move Sydney to Down in their CRDTs:
          Paris's CRDT: {paris: Up, sydney: Down, spain: Up}
          Spain's CRDT: {paris: Up, sydney: Down, spain: Up}
        Gossip confirms — CRDTs already agree (CRDT merge is idempotent)

  Both recompute: deterministic_assign([paris, spain], [P0..P5])
    → Paris: P0,P1,P2 | Spain: P3,P4,P5
  P0, P1, P4, P5 stay (sticky ✓). P2, P3 redistributed.
```

Note: φ-accrual adapts per peer — Paris→Sydney at 300ms RTT gets a longer leash than Paris→Spain at 100ms. Fewer false positives.

**C — Sydney Returns:**

```
  Sydney starts, gossips {sydney: Up} to a random peer (Paris)
  Paris merges: {paris: Up, sydney: Up, spain: Up}
  Gossip converges in 2-3 rounds
  All recompute: back to Paris: P0,P1 | Sydney: P2,P3 | Spain: P4,P5 (sticky ✓)
```

**D — Tokyo Joins:**

```
  Tokyo starts, gossips {tokyo: Up} to a seed node (Paris)
  Gossip converges: {paris: Up, sydney: Up, spain: Up, tokyo: Up}
  All 4 recompute: deterministic_assign with 4 workers
  Stickiness depends on the deterministic function (HRW = good, round-robin = bad)
```

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

### Scenario Walkthrough

**A — Cold Start:**

```
  KurrentDB partition pool: [P0: free] [P1: free] [P2: free] [P3: free] [P4: free] [P5: free]

  Paris, Sydney, Spain all start and race to claim:

  Paris  writes: ClaimPartition(P0, worker=paris)  ──► v0 ✓ (first writer wins)
  Sydney writes: ClaimPartition(P0, worker=sydney) ──► CONFLICT (Paris already claimed)
  Sydney retries on P1: ClaimPartition(P1)          ──► v1 ✓
  Spain  writes: ClaimPartition(P2)                 ──► v2 ✓
  Paris  writes: ClaimPartition(P3)                 ──► v3 ✓
  Sydney writes: ClaimPartition(P4)                 ──► v4 ✓
  Spain  writes: ClaimPartition(P5)                 ──► v5 ✓

  Result: Paris: P0,P3 | Sydney: P1,P4 | Spain: P2,P5
  Even-ish, but which specific partitions go where is non-deterministic
  (depends on who writes faster — latency-biased).
```

**B — Sydney Crashes:**

```
  Sydney stops heartbeating P1 and P4.
  After timeout (e.g., 5s):
    P1 and P4 marked as "orphaned" in KurrentDB stream.

  Paris and Spain race to steal:
    Paris  writes: StealPartition(P1, worker=paris)  ──► ✓
    Spain  writes: StealPartition(P1, worker=spain)  ──► CONFLICT
    Spain  writes: StealPartition(P4, worker=spain)  ──► ✓

  Result: Paris: P0,P1,P3 | Spain: P2,P4,P5
  P0, P2, P3, P5 stay (sticky ✓). P1, P4 redistributed (whoever was fastest).
```

**C — Sydney Returns:**

```
  Pool has no free partitions. Sydney must wait for rebalance.
  No automatic rebalance — nobody "gives back" partitions.
  Sydney sits idle unless another mechanism triggers redistribution.

  NOT sticky — Sydney doesn't get P1, P4 back automatically.
  Would need a separate "rebalance" protocol on top.
```

**D — Tokyo Joins:**

```
  Same problem as C — no free partitions, Tokyo sits idle.
  Someone must voluntarily release partitions, or a periodic
  rebalance must redistribute. Work stealing only handles the
  initial grab and failure recovery, not scale-out.
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

## Scenario Results Summary

How each option handles the 4 shared scenarios:

### A — Cold Start (3 workers, 6 partitions)

| Option | How assignment happens | Who decides | Time to first assignment |
|---|---|---|---|
| 1. Bully | Election → leader assigns | Spain (highest ID) | Election rounds + 1 write (~2-3s geo) |
| 2. Raft | Election → leader proposes → majority commits | Spain (Raft leader) | Election + 1 commit round (~2-4s geo) |
| 3. KurrentDB Log | Workers write join events → all compute same result | Nobody (deterministic function) | Join events propagate (~1s) |
| 4. Hash Ring | Workers join ring → local computation | Nobody (hash function) | Membership propagation (~1s) |
| 5. Protocol Actors | Any worker proposes → majority votes → commit | First proposer | 2 round-trips (~1-2s geo) |
| 6. Rendezvous (HRW) | Workers agree on member list → local computation | Nobody (hash function) | Membership propagation (~1s) |
| 7. Gossip+CRDT | Gossip converges → local computation | Nobody (deterministic function) | 2-3 gossip rounds (~600ms-1s) |
| 8. Work Stealing | Workers race to claim partitions | Whoever writes fastest | Immediate (progressive) |

### B — Sydney Crashes

| Option | Detection method | Detection time | Partitions moved | Sticky? | Balanced? |
|---|---|---|---|---|---|
| 1. Bully | Leader's heartbeat timeout | ~2s | 2 (P2, P3) | Yes | Leader controls (can balance) |
| 2. Raft | Raft heartbeat timeout | ~2-5s | 2 (P2, P3) | Yes | Leader controls (can balance) |
| 3. KurrentDB Log | Heartbeat events stop → any worker writes WorkerLeft | ~5-10s | 2 (P2, P3) | Depends on function | Depends on function |
| 4. Hash Ring | Membership update removes Sydney from ring | Depends on membership mechanism | 2 (P1, P2) | Yes | No — all go to neighbor |
| 5. Protocol Actors | Heartbeat timeout → proposer triggers rebalance | ~2-3s | 2 (P2, P3) | Yes | Proposer controls (can balance) |
| 6. Rendezvous (HRW) | Membership update removes Sydney | Depends on membership mechanism | 2 (P1, P3) | Yes | No — next-highest gets them |
| 7. Gossip+CRDT | φ-accrual failure detector | Adaptive (~2-5s) | 2 (P2, P3) | Depends on function | Depends on function |
| 8. Work Stealing | Heartbeat timeout on claims | ~5s | 2 (P1, P4) | Yes | No — fastest stealer wins |

### C — Sydney Returns

| Option | How Sydney re-enters | Gets original partitions back? | Disruption to others |
|---|---|---|---|
| 1. Bully | Leader detects heartbeat → reassigns | Yes (leader can optimize for stickiness) | Minimal — only moved partitions return |
| 2. Raft | Joins Raft group → leader reassigns | Yes (leader can optimize) | Minimal |
| 3. KurrentDB Log | Writes WorkerJoined → all recompute | If function is stable, yes | Minimal if function is stable |
| 4. Hash Ring | Re-added to ring at same position | Yes — same hash = same position | Zero — only Sydney's partitions move |
| 5. Protocol Actors | Detected → proposer reassigns | Yes (proposer can optimize) | Minimal |
| 6. Rendezvous (HRW) | Re-added to member list, recompute | Yes — same hashes = same result | Zero — only Sydney's partitions return |
| 7. Gossip+CRDT | Gossips {sydney: Up} → all recompute | If function is stable, yes | Minimal |
| 8. Work Stealing | No free partitions — Sydney sits idle | No — must wait for rebalance | None (but Sydney has no work!) |

### D — Tokyo Joins (4th worker)

| Option | Partitions moved | Who loses partitions? | Sticky for existing? |
|---|---|---|---|
| 1. Bully | 2 (leader decides) | Leader picks who gives up | Yes — leader minimizes movement |
| 2. Raft | 2 (leader proposes) | Leader picks | Yes |
| 3. KurrentDB Log | Depends on function | Depends on function | If using HRW: yes |
| 4. Hash Ring | 1 (P3 only in our example) | Only the neighbor | Yes — other workers untouched |
| 5. Protocol Actors | 2 (proposer decides) | Proposer picks | Yes |
| 6. Rendezvous (HRW) | 2 (P1, P4 in our example) | Whoever Tokyo out-hashes | Yes — non-affected stay |
| 7. Gossip+CRDT | Depends on function | Depends on function | Depends on function |
| 8. Work Stealing | 0 — Tokyo sits idle | Nobody | N/A — Tokyo has no work |

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

### How the Ring Works — 3 Workers, 6 Partitions

The ring is a circle of numbers from 0 to 99 (in reality 0 to 2^32-1, but let's use 0-99 to keep it simple). Everything — workers AND partitions — gets hashed onto this circle.

**Step 1 — Hash the workers onto the ring:**

```
  hash("paris")    = 15
  hash("sydney")   = 48
  hash("spain") = 79
```

Place them on the circle:

```
                       0
                       │
                  15 Paris
                /           \
              /               \
    79 Spain            48 Sydney
              \               /
                \           /
                  ─────────
                      50
```

**Step 2 — Hash the partitions onto the ring:**

```
  hash("P0") = 5
  hash("P1") = 22
  hash("P2") = 37
  hash("P3") = 55
  hash("P4") = 68
  hash("P5") = 90
```

Now place everything on the same circle:

```
                         0
                    P0(5)│
                  15 Paris
                /  P1(22)    \
              /    P2(37)      \
    79 Spain            48 Sydney
        P5(90)\    P3(55)      /
               \   P4(68)    /
                  ─────────
                      50
```

**Step 3 — The assignment rule: walk clockwise, first worker you hit owns it.**

Starting from each partition's position, walk clockwise around the ring. The first worker you bump into owns that partition:

```
  P0 at 5  ──clockwise──► Paris at 15     ✓ Paris owns P0
  P1 at 22 ──clockwise──► Sydney at 48    ✓ Sydney owns P1
  P2 at 37 ──clockwise──► Sydney at 48    ✓ Sydney owns P2
  P3 at 55 ──clockwise──► Spain at 79  ✓ Spain owns P3
  P4 at 68 ──clockwise──► Spain at 79  ✓ Spain owns P4
  P5 at 90 ──clockwise──► (wrap!) Paris at 15  ✓ Paris owns P5
```

Note P5: at position 90, walking clockwise goes 91, 92, ... 99, 0, 1, ... 15 — wraps around to Paris.

**Final assignment:**

```
  Paris:    P0, P5      (2 partitions)
  Sydney:   P1, P2      (2 partitions)
  Spain: P3, P4      (2 partitions)
```

Perfectly even here. In practice with real hash functions, it won't always be this clean — that's what virtual nodes fix (more on that below).

**Step 4 — Sydney crashes. What happens?**

Remove Sydney (position 48) from the ring. Now P1 and P2 need new owners. Walk clockwise from their positions again:

```
  P0 at 5  ──clockwise──► Paris at 15      (unchanged)
  P1 at 22 ──clockwise──► Spain at 79   ← was Sydney, now Spain
  P2 at 37 ──clockwise──► Spain at 79   ← was Sydney, now Spain
  P3 at 55 ──clockwise──► Spain at 79   (unchanged)
  P4 at 68 ──clockwise──► Spain at 79   (unchanged)
  P5 at 90 ──clockwise──► Paris at 15      (unchanged)
```

```
  Before:                          After Sydney dies:
  ─────────                        ─────────────────
  Paris:    P0, P5  (2)            Paris:    P0, P5          (unchanged ✓)
  Sydney:   P1, P2  (2)  ──►      Spain: P1, P2, P3, P4  (got Sydney's)
  Spain: P3, P4  (2)

  Moved: P1, P2 (only Sydney's partitions)
  Stayed: P0, P3, P4, P5 (everyone else's partitions — untouched)
```

This is the key property: **only the dead worker's partitions move.** Paris doesn't care that Sydney died — its partitions are unaffected.

But notice the problem: Spain now has 4 partitions, Paris has 2. The load is uneven. With a real hash ring this gets worse — the next clockwise neighbor always absorbs ALL of the dead worker's load instead of spreading it.

**Step 5 — Sydney comes back. What happens?**

Re-add Sydney at position 48:

```
  P1 at 22 ──clockwise──► Sydney at 48    ← back to Sydney
  P2 at 37 ──clockwise──► Sydney at 48    ← back to Sydney
```

Everything returns to exactly what it was. **Same hash, same position, same assignment.** This is the stickiness property — the assignment is a pure function of the ring state.

**Step 6 — New worker Tokyo joins at position 60.**

```
  hash("tokyo") = 60
```

Walk clockwise from every partition again:

```
  P0 at 5  ──clockwise──► Paris at 15      (unchanged)
  P1 at 22 ──clockwise──► Sydney at 48     (unchanged)
  P2 at 37 ──clockwise──► Sydney at 48     (unchanged)
  P3 at 55 ──clockwise──► Tokyo at 60      ← was Spain, now Tokyo
  P4 at 68 ──clockwise──► Spain at 79   (unchanged)
  P5 at 90 ──clockwise──► Paris at 15      (unchanged)
```

```
  Before:                          After Tokyo joins:
  ─────────                        ─────────────────
  Paris:    P0, P5  (2)            Paris:    P0, P5  (2)     (unchanged ✓)
  Sydney:   P1, P2  (2)           Sydney:   P1, P2  (2)     (unchanged ✓)
  Spain: P3, P4  (2)           Spain: P4       (1)     (lost P3)
                                   Tokyo:    P3       (1)     (got P3 from Spain)

  Moved: P3 only (1 partition!)
  Stayed: P0, P1, P2, P4, P5 (5 of 6 partitions — untouched)
```

Tokyo "steals" only the partitions that fall between it and the previous worker counter-clockwise (Spain). Minimal disruption.

**Why this matters for our use case:** When a new worker joins your geo cluster, it doesn't cause a full rebalance. Only a fraction of partitions (roughly 1/N) move to the new worker. Workers that were happily processing their partitions continue without interruption.

### The Virtual Node Problem

In our clean example, each worker got exactly 2 partitions. That was luck. With real hash functions the ring arcs between workers are unequal. Imagine instead:

```
  hash("paris")    = 10
  hash("sydney")   = 15    ← only 5 apart from Paris!
  hash("spain") = 80

  Paris owns:     arc 80→10 = 30% of the ring
  Sydney owns:    arc 10→15 = 5% of the ring     ← barely anything!
  Spain owns:  arc 15→80 = 65% of the ring    ← overloaded
```

With 6 partitions, Spain would likely get 4, Paris would get 2, Sydney might get 0. Terrible distribution.

**Solution: Virtual nodes.** Instead of placing each worker at 1 position, place them at **many** positions:

```
  Paris gets 4 virtual nodes:
    hash("paris-vn0") = 10
    hash("paris-vn1") = 35
    hash("paris-vn2") = 62
    hash("paris-vn3") = 88

  Sydney gets 4 virtual nodes:
    hash("sydney-vn0") = 15
    hash("sydney-vn1") = 42
    hash("sydney-vn2") = 71
    hash("sydney-vn3") = 95

  Spain gets 4 virtual nodes:
    hash("spain-vn0") = 22
    hash("spain-vn1") = 50
    hash("spain-vn2") = 80
    hash("spain-vn3") = 3

  Ring now has 12 points instead of 3:

  Pos:  3(V)  10(P)  15(S)  22(V)  35(P)  42(S)  50(V)  62(P)  71(S)  80(V)  88(P)  95(S)
        ─────────────────────────────────────────────────────────────────────────────────────►

  Arcs: V=7  P=5   S=7    V=13   P=7    S=8    V=12   P=9    S=9    V=8    P=7    S=5
```

Now the ring is much more evenly divided. Each worker's total arc coverage is roughly 33%. The more virtual nodes, the more even it gets.

**How partition assignment works with virtual nodes:** Same rule — walk clockwise, first virtual node you hit determines the owner. If you hit `sydney-vn2`, Sydney owns that partition. The "virtual" part is just for placement; ownership maps back to the real worker.

**Trade-offs of virtual node count:**

| Virtual Nodes/Worker | Distribution Evenness | Ring Size (3 workers) | Rebalance Granularity |
|---|---|---|---|
| 1 | Terrible (high variance) | 3 points | Coarse — whole arc moves |
| 10 | Poor | 30 points | Moderate |
| 100 | Good (~5% variance) | 300 points | Fine |
| 150+ | Excellent (~1-2% variance) | 450+ points | Very fine |

This is the **main tuning knob** of consistent hashing — and the thing that makes rendezvous hashing (Option 6) attractive. HRW needs no virtual nodes and achieves even distribution naturally by computing a hash for every (partition, worker) pair.

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
