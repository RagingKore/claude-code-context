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

### Key Groups (Virtual Partitions)

KurrentDB does not have native partitions. Instead, the Kurrent Processors project introduces **key groups** — virtual partitions computed by hashing the stream name:

```
  stream name  ──►  hash(stream_name) mod key_group_count  ──►  key group ID
```

- **Stream "order-12345"** → `hash("order-12345") mod 128` → key group 47
- **Stream "order-67890"** → `hash("order-67890") mod 128` → key group 12
- **All streams in key group 47** are processed by whoever owns key group 47

The key group count is a **fixed architectural parameter** (default: **128**). It determines:

| Property | Impact |
|---|---|
| **Maximum parallelism** | Key group count = absolute ceiling on useful workers (128 KGs → 128th worker is the last one that gets work) |
| **Rebalance granularity** | More key groups = finer-grained, smoother rebalancing |
| **Distribution quality** | Depends heavily on the KG-to-worker ratio (see below) |
| **Hot stream isolation** | Two hot streams in the same key group can't be separated — more key groups reduces collision probability |

**Changing the key group count is a breaking change** — every stream rehashes to a different key group, invalidating all existing assignments and checkpoints. Over-provision upfront.

### The 1:1 Ratio Problem

**128 workers is an expected production scenario.** With 128 key groups and 128 workers, each worker should get exactly 1 key group. But hash-based assignment (Options 4, 6) **cannot guarantee this** — it's the classic "balls into bins" problem:

```
  128 KGs randomly assigned to 128 workers (Poisson λ=1):

  Workers getting 0 KGs:  ~47  (36.8%)  ← idle, wasting resources!
  Workers getting 1 KG:   ~47  (36.8%)  ← correct
  Workers getting 2 KGs:  ~24  (18.4%)  ← overloaded
  Workers getting 3+ KGs:  ~10 ( 8.0%)  ← heavily overloaded
```

This isn't a bug in the hash function — it's a mathematical certainty. When the number of items equals the number of buckets, random assignment leaves ~37% of buckets empty. Virtual nodes (Option 4) don't help: they make the ring arcs even, but 128 key groups across 128 evenly-sized arcs is still 128 balls into 128 bins.

**Which options handle 1:1?**

| Option | 1:1 distribution | Why |
|---|---|---|
| 1. Bully | **Exact** ✓ | Leader assigns precisely 1 per worker |
| 2. Raft | **Exact** ✓ | Leader assigns precisely 1 per worker |
| 3. KurrentDB Log | **Depends** | Sorted round-robin = exact. HRW = broken. |
| 4. Hash Ring | **Broken** ✗ | ~47 idle workers, ~10 with 3+ KGs |
| 5. Protocol Actors | **Exact** ✓ | Proposer assigns precisely 1 per worker |
| 6. Rendezvous (HRW) | **Broken** ✗ | ~47 idle workers, ~10 with 3+ KGs |
| 7. Gossip+CRDT | **Depends** | Same as whatever assignment function is used |
| 8. Work Stealing | **Exact** ✓ | Workers claim 1 each, stop when pool is empty |

**Higher KG counts mitigate but don't eliminate the problem:**

| Key Groups | Workers | Ratio | Idle workers | Variance |
|---|---|---|---|---|
| 128 | 128 | 1:1 | **~47 (37%)** | Unusable for hash-based |
| 256 | 128 | 2:1 | ~17 (13%) | Poor |
| 512 | 128 | 4:1 | ~2 (2%) | Acceptable |
| 1024 | 128 | 8:1 | ~0 (0.03%) | Good — ±2.8 KGs/worker (~35% variance) |

> **Key group count recommendation:** If 128 workers is expected, **1024 key groups** provides 8:1 ratio with near-zero idle workers and ~35% distribution variance. 512 is the absolute minimum for hash-based options. If using leader-based assignment (Options 1, 2, 5), even 128 KGs works perfectly — the leader assigns exactly 1 per worker.

> **Impact on this analysis:** The scenario walkthroughs below use 6 simplified partitions (P0-P5) to keep examples readable. Notes are added where the key group count materially changes the outcome.

---

## Scenarios

Every option below is evaluated against the same set of scenarios so they can be compared side by side.

### Setup

> **Simplified model:** Production uses 128 key groups. These scenarios use 6 partitions (P0-P5) to keep walkthroughs readable. Where the key group count materially changes the outcome, it's called out.

```
Workers:     Paris (ID: 1)     Sydney (ID: 2)     Spain (ID: 3)
Partitions:  P0   P1   P2   P3   P4   P5    (representing 128 key groups at small scale)

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

Using SHA-256 mod 100 as the hash function. Real computed positions:

```
  SHA-256 mod 100:
  hash("paris")  = 99    hash("sydney") = 66    hash("spain") = 4

  hash("P0") = 94    hash("P1") = 66    hash("P2") = 34
  hash("P3") = 47    hash("P4") = 54    hash("P5") = 49
```

**A — Cold Start:**

```
  Workers on ring: Spain(4) ──── Sydney(66) ──── Paris(99)
  Rule: walk clockwise, first worker you hit owns it.

  P2(34) → Sydney(66) ✓     P3(47) → Sydney(66) ✓     P5(49) → Sydney(66) ✓
  P4(54) → Sydney(66) ✓     P1(66) → Sydney(66) ✓     P0(94) → Paris(99) ✓

  Result: Paris: P0 (1) | Sydney: P1,P2,P3,P4,P5 (5) | Spain: — (0)
```

⚠️ **This is terrible distribution.** The 3 workers cluster at positions 4, 66, 99 — Spain→Sydney covers a 62-unit arc (most of the ring), but Sydney→Paris is only 33 units and Paris→Spain (wrapping) is only 5 units. This is exactly why **virtual nodes** are essential — with only 3 points on the ring, hash clustering creates highly uneven arcs. (See [Appendix A](#appendix-a-consistent-hashing-deep-dive) for virtual node details.)

**B — Sydney Crashes:**

```
  Remove Sydney(66) from ring. Workers: Spain(4), Paris(99).
  Re-walk clockwise — ALL partitions land between 4 and 99:

  P2(34) → Paris(99)     P3(47) → Paris(99)     P5(49) → Paris(99)
  P4(54) → Paris(99)     P1(66) → Paris(99)     P0(94) → Paris(99)

  Result: Paris: P0,P1,P2,P3,P4,P5 (6!) | Spain: — (0!)

  Even worse — Paris absorbs ALL 6 partitions.
  Spain's arc (99→4, wrapping) is only 5 units wide.
```

**C — Sydney Returns:**

```
  Re-add Sydney(66). Same hash = same position = same assignment.
  Result: Paris: P0 | Sydney: P1,P2,P3,P4,P5 | Spain: —
  Perfectly sticky ✓ — but still terribly uneven.
```

**D — Tokyo Joins:**

```
  hash("tokyo") = 48. Inserted on ring: Spain(4) ── Tokyo(48) ── Sydney(66) ── Paris(99).

  P2(34) → Tokyo(48) ← was Sydney     P3(47) → Tokyo(48) ← was Sydney
  P5(49) → Sydney(66)                  P4(54) → Sydney(66)
  P1(66) → Sydney(66)                  P0(94) → Paris(99)

  Result: Paris: P0 (1) | Sydney: P1,P4,P5 (3) | Tokyo: P2,P3 (2) | Spain: — (0)
  Tokyo absorbs some of Sydney's overload, but Spain still gets nothing.
```

> **Bottom line:** Consistent hash ring with raw SHA-256 and no virtual nodes produces wildly uneven distribution at small scale. With 150+ virtual nodes per worker, distribution becomes even — but that's significant added complexity. This is the primary trade-off vs. HRW (Option 6).
>
> **With more key groups:** Distribution improves with the KG-to-worker ratio, but two structural problems persist: (1) the cascading failure problem — a dead worker's key groups all dump onto one clockwise neighbor regardless of KG count, and (2) **at 1:1 ratio (e.g., 128 KGs with 128 workers), ~47 workers get nothing** (see [The 1:1 Ratio Problem](#the-11-ratio-problem)). Virtual nodes don't help with #2 — it's a fundamental balls-into-bins limitation. Needs at least 8:1 KG-to-worker ratio (1024 KGs) for acceptable distribution at 128 workers.

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
- **Cannot guarantee 1:1 at max scale** — at 128 KGs with 128 workers, ~47 workers get nothing (balls-into-bins). Requires high KG/worker ratio (8:1+) or virtual nodes can't save it

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

Using SHA-256 of `"partition:worker"`, taking the first 8 hex chars as an integer score. Highest score wins.

```
  Workers agree on member list: [paris, sydney, spain]

  P0: SHA256("P0:paris")=0xb0f2ca01  SHA256("P0:sydney")=0xde678c0a  SHA256("P0:spain")=0x9a700307
      → Sydney (0xde678c0a highest) ✓
  P1: SHA256("P1:paris")=0x64eea4d0  SHA256("P1:sydney")=0x8ee49e90  SHA256("P1:spain")=0xf1002873
      → Spain (0xf1002873 highest) ✓
  P2: SHA256("P2:paris")=0x222b70fa  SHA256("P2:sydney")=0xb1b8c658  SHA256("P2:spain")=0x47cfca6d
      → Sydney (0xb1b8c658 highest) ✓
  P3: SHA256("P3:paris")=0xd86ce171  SHA256("P3:sydney")=0xe04e7d4a  SHA256("P3:spain")=0x3f1003d6
      → Sydney (0xe04e7d4a highest) ✓
  P4: SHA256("P4:paris")=0xb973cb6e  SHA256("P4:sydney")=0x745017c2  SHA256("P4:spain")=0xee7cbe82
      → Spain (0xee7cbe82 highest) ✓
  P5: SHA256("P5:paris")=0xc187e9ca  SHA256("P5:sydney")=0x0155b20c  SHA256("P5:spain")=0xbd93b3bc
      → Paris (0xc187e9ca highest) ✓

  Result: Paris: P5 (1) | Sydney: P0,P2,P3 (3) | Spain: P1,P4 (2)
```

Note: with only 6 partitions, a perfectly even 2-2-2 split is unlikely with real hashes. The 3-2-1 distribution is typical variance for small partition counts. With a **high KG-to-worker ratio** (e.g., 1024 KGs / 3 workers ≈ 341 each), HRW converges toward even distribution naturally. At 1:1 ratio (128 KGs / 128 workers), HRW fails — see [The 1:1 Ratio Problem](#the-11-ratio-problem).

**B — Sydney Crashes:**

```
  Remove Sydney from member list. Recompute only Sydney's partitions:

  P0: was Sydney. Remaining: paris=0xb0f2ca01  spain=0x9a700307 → Paris ✓
  P2: was Sydney. Remaining: paris=0x222b70fa  spain=0x47cfca6d → Spain ✓
  P3: was Sydney. Remaining: paris=0xd86ce171  spain=0x3f1003d6 → Paris ✓

  Result: Paris: P0,P3,P5 (3) | Spain: P1,P2,P4 (3)

  Only P0, P2, P3 moved. P1, P4, P5 untouched (sticky ✓).
  Perfect 3-3 split! Sydney's partitions distributed to BOTH survivors
  based on their individual next-highest scores — not all to one neighbor.
```

This is a key advantage over the hash ring: when a worker dies, its partitions spread across the remaining workers based on independent hash scores, rather than all piling onto one clockwise neighbor.

**C — Sydney Returns:**

```
  Add Sydney back to member list. Recompute:

  P0: sydney=0xde678c0a is still highest → Sydney ✓
  P2: sydney=0xb1b8c658 is still highest → Sydney ✓
  P3: sydney=0xe04e7d4a is still highest → Sydney ✓

  Result: Paris: P5 (1) | Sydney: P0,P2,P3 (3) | Spain: P1,P4 (2)
  Perfectly sticky ✓ — same members = same hashes = same assignment.
```

**D — Tokyo Joins:**

```
  Add Tokyo. Recompute all — does Tokyo's score beat the current winner?

  P0: tokyo=0x33dafb33 vs sydney=0xde678c0a → Sydney still wins ✓ (unchanged)
  P1: tokyo=0xc419ce76 vs spain=0xf1002873  → Spain still wins ✓ (unchanged)
  P2: tokyo=0x1c50acf0 vs sydney=0xb1b8c658 → Sydney still wins ✓ (unchanged)
  P3: tokyo=0x176c5374 vs sydney=0xe04e7d4a → Sydney still wins ✓ (unchanged)
  P4: tokyo=0x151e3aed vs spain=0xee7cbe82  → Spain still wins ✓ (unchanged)
  P5: tokyo=0x0d0292d5 vs paris=0xc187e9ca  → Paris still wins ✓ (unchanged)

  Result: Paris: P5 (1) | Sydney: P0,P2,P3 (3) | Spain: P1,P4 (2) | Tokyo: — (0!)
  Zero partitions moved — Tokyo gets nothing!
```

⚠️ **Small-N problem (6 partitions only):** With only 6 partitions, there's a real chance a new worker's hash scores don't beat any existing winner. Tokyo's scores are consistently lower.

> **With high KG-to-worker ratios: this problem disappears.** A 4th worker joining a 3-worker cluster with 1024 KGs will statistically win ~256. But **at 1:1 ratio (128 KGs, 128 workers), HRW leaves ~47 workers idle** — the same balls-into-bins problem as the hash ring (see [The 1:1 Ratio Problem](#the-11-ratio-problem)). HRW needs at least 8:1 KG-to-worker ratio for reliable distribution.

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
- **Cannot guarantee 1:1 at max scale** — at 128 KGs with 128 workers, ~47 workers get nothing (balls-into-bins). Requires high KG/worker ratio (8:1+) for reliable distribution

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
  Pool has no free partitions. Sydney announces itself:
    Sydney writes: WorkerJoined(worker=sydney)  ──► v20

  Rebalance trigger: all workers subscribe to the coordination stream.
  Paris and Spain see the new worker and check load balance:
    target = ceil(6 / 3) = 2 partitions per worker
    Paris  has 3 (P0,P1,P3) — over target by 1
    Spain  has 3 (P2,P4,P5) — over target by 1

  Voluntary release protocol:
    Each overloaded worker independently picks its LAST-claimed partition to release:
    Paris  writes: ReleasePartition(P1, worker=paris)   ──► v21
    Spain  writes: ReleasePartition(P4, worker=spain)   ──► v22

  Sydney sees free partitions and claims them:
    Sydney writes: ClaimPartition(P1, worker=sydney)    ──► v23 ✓
    Sydney writes: ClaimPartition(P4, worker=sydney)    ──► v24 ✓

  Result: Paris: P0,P3 | Sydney: P1,P4 | Spain: P2,P5
  Balanced ✓ — but NOT sticky (Sydney gets P1,P4 not its original partitions).
  Which partitions Sydney gets depends on release timing, not on history.
```

The release protocol adds complexity: workers must agree on the target load, decide which partitions to release, and coordinate the release/claim sequence. This is effectively a mini-rebalancing protocol layered on top of work stealing.

**D — Tokyo Joins:**

```
  Tokyo writes: WorkerJoined(worker=tokyo)  ──► v30

  Rebalance trigger: all workers check load balance:
    target = ceil(6 / 4) = 2 partitions per worker (but 6/4 = 1.5, so some get 2, some get 1)
    max_per_worker = ceil(6 / 4) = 2
    Paris  has 2 (P0,P3) — at target, no release
    Sydney has 2 (P1,P4) — at target, no release
    Spain  has 2 (P2,P5) — at target, no release
    Tokyo  has 0 — under target

  Problem: nobody is OVER target, so nobody releases voluntarily!
  Need a tie-breaking rule: "if a worker with 0 partitions exists AND
  you hold max_per_worker, release your last-claimed partition."

  Revised: target when idle workers exist = floor(6 / 4) = 1 for some workers.
    Workers holding > floor(6/4) partitions release one:
    Paris  writes: ReleasePartition(P3, worker=paris)   ──► v31
    Sydney writes: ReleasePartition(P4, worker=sydney)  ──► v32

  Tokyo claims:
    Tokyo writes: ClaimPartition(P3, worker=tokyo)      ──► v33 ✓
    Tokyo writes: ClaimPartition(P4, worker=tokyo)      ──► v34 ✓

  Result: Paris: P0 (1) | Sydney: P1 (1) | Spain: P2,P5 (2) | Tokyo: P3,P4 (2)
  Balanced ✓ (some have 1, some have 2 — correct for 6/4).
  But: release decisions are non-deterministic — different workers could release
  different partitions depending on timing, creating flapping risk.
```

> **Scale-out complexity:** The voluntary release protocol requires (1) all workers agree on who is overloaded (consensus on member list + partition counts), (2) overloaded workers decide which partitions to shed (deterministic pick avoids flapping), (3) released partitions are claimed atomically (avoid thundering herd). This layered protocol significantly increases complexity and starts resembling a leader-based assignment system — undermining the "no coordination" benefit of work stealing.

### Assignment Flow

1. Partitions start as unclaimed entries in a KurrentDB stream
2. Workers race to claim partitions via optimistic concurrency writes (`ClaimPartition` events with expected version)
3. First writer wins — other workers retry on a different unclaimed partition
4. Workers heartbeat their claims periodically
5. If a worker stops heartbeating, its partitions become stealable after a timeout
6. Idle or less-loaded workers steal orphaned partitions
7. **Scale-out:** When a new worker joins, overloaded workers voluntarily release partitions (target = `ceil(partitions / workers)`). New worker claims released partitions. Requires a `WorkerJoined` event and coordinated release/claim sequence
8. **Release tie-breaking:** Workers release their most-recently-claimed partition first (LIFO), reducing disruption to long-running processing

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
- **Scale-out requires layered protocol** — voluntary release on worker join needs consensus on member list + load counts + deterministic partition selection. This layered coordination undermines the "no upfront coordination" benefit

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
| **Voluntary release on scale-out** | Overloaded workers shed partitions when new worker joins |
| **Contention under simultaneous startup** | 3 workers starting at once don't thrash |

---

## Scenario Results Summary

How each option handles the 4 shared scenarios:

### A — Cold Start (3 workers, 6 partitions / 128 key groups in production)

| Option | How assignment happens | Who decides | Distribution | Time to first assignment |
|---|---|---|---|---|
| 1. Bully | Election → leader assigns | Spain (highest ID) | 2-2-2 (leader controls) | Election rounds + 1 write (~2-3s geo) |
| 2. Raft | Election → leader proposes → majority commits | Spain (Raft leader) | 2-2-2 (leader controls) | Election + 1 commit round (~2-4s geo) |
| 3. KurrentDB Log | Workers write join events → all compute same result | Nobody (deterministic function) | Depends on function | Join events propagate (~1s) |
| 4. Hash Ring | Workers join ring → local computation | Nobody (hash function) | **1-5-0** ⚠️ (6 KGs, no vnodes) / even at high ratio, **broken at 1:1** | Membership propagation (~1s) |
| 5. Protocol Actors | Any worker proposes → majority votes → commit | First proposer | 2-2-2 (proposer controls) | 2 round-trips (~1-2s geo) |
| 6. Rendezvous (HRW) | Workers agree on member list → local computation | Nobody (hash function) | **1-3-2** (6 KGs) / even at high ratio, **broken at 1:1** | Membership propagation (~1s) |
| 7. Gossip+CRDT | Gossip converges → local computation | Nobody (deterministic function) | Depends on function | 2-3 gossip rounds (~600ms-1s) |
| 8. Work Stealing | Workers race to claim partitions | Whoever writes fastest | ~2-2-2 (non-deterministic) | Immediate (progressive) |

### B — Sydney Crashes

| Option | Detection method | Detection time | Partitions moved | Sticky? | Balanced after? |
|---|---|---|---|---|---|
| 1. Bully | Leader's heartbeat timeout | ~2s | 2 (P2, P3) | Yes | 3-3 (leader controls) |
| 2. Raft | Raft heartbeat timeout | ~2-5s | 2 (P2, P3) | Yes | 3-3 (leader controls) |
| 3. KurrentDB Log | Heartbeat events stop → any worker writes WorkerLeft | ~5-10s | 2 (P2, P3) | Depends on function | Depends on function |
| 4. Hash Ring | Membership update removes Sydney | Depends on mechanism | **5** (all Sydney's → Paris) ⚠️ | Yes | **6-0** ⚠️ (without vnodes). 128 KGs: still cascades to neighbor |
| 5. Protocol Actors | Heartbeat timeout → proposer triggers rebalance | ~2-3s | 2 (P2, P3) | Yes | 3-3 (proposer controls) |
| 6. Rendezvous (HRW) | Membership update removes Sydney | Depends on mechanism | 3 (P0, P2, P3) | Yes | **3-3** ✓ (spread across both) |
| 7. Gossip+CRDT | φ-accrual failure detector | Adaptive (~2-5s) | Depends on function | Depends on function | Depends on function |
| 8. Work Stealing | Heartbeat timeout on claims | ~5s | 2 (P1, P4) | Yes | ~3-3 (fastest stealer wins) |

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
| 8. Work Stealing | Writes WorkerJoined → overloaded workers release | No — gets whatever is released (LIFO) | 2 workers release 1 partition each |

### D — Tokyo Joins (4th worker)

| Option | Partitions moved | Who loses partitions? | Sticky for existing? |
|---|---|---|---|
| 1. Bully | 2 (leader decides) | Leader picks who gives up | Yes — leader minimizes movement |
| 2. Raft | 2 (leader proposes) | Leader picks | Yes |
| 3. KurrentDB Log | Depends on function | Depends on function | If using HRW: yes |
| 4. Hash Ring | 2 (P2, P3 → Tokyo) | Sydney loses 2 | Yes — other workers untouched |
| 5. Protocol Actors | 2 (proposer decides) | Proposer picks | Yes |
| 6. Rendezvous (HRW) | **0** ⚠️ (6 KGs) / ~K/N at high ratio / **broken at 1:1** | Nobody (6 KGs) / spread at high ratio | Even at high KG/worker ratio; ~37% idle at 1:1 |
| 7. Gossip+CRDT | Depends on function | Depends on function | Depends on function |
| 8. Work Stealing | 2 (voluntary release by overloaded workers) | Workers holding > floor(6/4) release | Partial — released partitions are non-deterministic |

---

## Comparison Matrix

| Criteria | 1. Bully | 2. Raft | 3. KurrentDB Log | 4. Hash Ring | 5. Protocol Actors | 6. Rendezvous (HRW) | 7. Gossip+CRDT | 8. Work Stealing |
|---|---|---|---|---|---|---|---|---|
| **Server coordination** | None | None | None | None | None | None | None | None |
| **Leases** | None | None | None | None | None | None | None | **Yes (hidden)** |
| **Strategy flexibility** | Any | Any | Deterministic | Hash only | Any | Hash + weighted | Deterministic | Capacity-biased |
| **Split-brain safety** | Term fencing | Built-in | N/A (no leader) | N/A | Epoch+majority | N/A | Crumbles safely | N/A (claim-based) |
| **Geo-latency impact** | Election slow | Heartbeat tuning | Write propagation | Membership prop. | 2 round-trips | Membership prop. | Gossip rounds | Claim race latency |
| **Complexity** | Medium | High | Low-Medium | Low | Medium | Low | Medium-High | Medium-High |
| **Single point of failure** | Leader (temp) | Leader (temp) | KurrentDB | None | None | None | None | KurrentDB |
| **Implementation effort** | Low | High | Medium | Low | Low | Low | High (or use Akka) | Medium |
| **Rebalance speed** | Instant (leader) | Majority commit | Event propagation | Instant (local) | 2-phase voting | Instant (local) | Gossip convergence | Steal timeout |
| **Actor framework fit** | Natural | Natural | DB-centric | Minimal | Very natural | Minimal | Built into Akka | Moderate |
| **Membership sub-problem** | Election | Raft log | KurrentDB stream | Needs solving | Part of protocol | Needs solving | Gossip (built-in) | KurrentDB heartbeats |
| **1:1 KG/worker distribution** | Exact ✓ | Exact ✓ | Depends on function | **Broken** (~37% idle) | Exact ✓ | **Broken** (~37% idle) | Depends on function | Exact ✓ |
| **Min KG/worker ratio** | 1:1 | 1:1 | Depends on function | 8:1+ | 1:1 | 8:1+ | Depends on function | 1:1 |

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

**For the hashing family** (Options 4 and 6), spike them together and compare. Rendezvous hashing (Option 6) is likely the better pick — it's simpler, has no virtual node tuning, and supports weighted distribution. See the [Consistent Hashing Deep Dive](#appendix-a-consistent-hashing-deep-dive) below for a thorough comparison. **However:** both Options 4 and 6 fundamentally cannot guarantee even distribution when workers ≈ key groups (the 1:1 ratio problem). At 128 workers with 128 key groups, ~37% of workers sit idle. This means either (a) the key group count must be 8x+ the max worker count (1024 KGs for 128 workers), or (b) hash-based options are only viable when combined with a leader/proposer that does the actual assignment using the hash as a hint.

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

The ring is a circle of numbers from 0 to 99 (in reality 0 to 2^32-1, but let's use SHA-256 mod 100 to keep it readable). Everything — workers AND partitions — gets hashed onto this circle.

**Step 1 — Hash the workers onto the ring:**

```
  SHA-256 mod 100:
  hash("paris")    = 99       (SHA-256: 1670f2e4...)
  hash("sydney")   = 66       (SHA-256: e8032604...)
  hash("spain")    = 4        (SHA-256: 4c799454...)
```

Place them on the circle:

```
                       0
                  4 Spain ──── 99 Paris
                /                      |
              /                        |
             /                         |
              \                        |
                \                    /
                  66 Sydney ────
                      50
```

Notice the problem already: Spain(4) and Paris(99) are only 5 units apart (wrapping). Sydney(66) sits alone covering a huge arc. This clustering is typical with only 3 hash points.

**Step 2 — Hash the partitions onto the ring:**

```
  SHA-256 mod 100:
  hash("P0") = 94    hash("P1") = 66    hash("P2") = 34
  hash("P3") = 47    hash("P4") = 54    hash("P5") = 49
```

**Step 3 — The assignment rule: walk clockwise, first worker you hit owns it.**

Starting from each partition's position, walk clockwise around the ring:

```
  P2 at 34 ──clockwise──► Sydney at 66    ✓ Sydney owns P2
  P3 at 47 ──clockwise──► Sydney at 66    ✓ Sydney owns P3
  P5 at 49 ──clockwise──► Sydney at 66    ✓ Sydney owns P5
  P4 at 54 ──clockwise──► Sydney at 66    ✓ Sydney owns P4
  P1 at 66 ──clockwise──► Sydney at 66    ✓ Sydney owns P1 (exact match)
  P0 at 94 ──clockwise──► Paris at 99     ✓ Paris owns P0
```

**Final assignment:**

```
  Paris:    P0          (1 partition)
  Sydney:   P1,P2,P3,P4,P5  (5 partitions!)
  Spain:    —           (0 partitions!)
```

⚠️ **This is terrible distribution** — and it's what real SHA-256 hashes actually produce with only 3 ring points. Sydney's arc (4→66, spanning 62 units) covers most of the ring. Spain's arc (99→4, spanning only 5 units wrapping around 0) is tiny. This is NOT bad luck — it's the fundamental problem that virtual nodes solve (see below).

**Step 4 — Sydney crashes. What happens?**

Remove Sydney (position 66) from the ring. Now all of Sydney's 5 partitions need new owners:

```
  P0 at 94 ──clockwise──► Paris at 99      (unchanged)
  P2 at 34 ──clockwise──► Paris at 99      ← was Sydney, now Paris
  P3 at 47 ──clockwise──► Paris at 99      ← was Sydney, now Paris
  P5 at 49 ──clockwise──► Paris at 99      ← was Sydney, now Paris
  P4 at 54 ──clockwise──► Paris at 99      ← was Sydney, now Paris
  P1 at 66 ──clockwise──► Paris at 99      ← was Sydney, now Paris
```

```
  Before:                          After Sydney dies:
  ─────────                        ─────────────────
  Paris:    P0          (1)        Paris:    P0,P1,P2,P3,P4,P5  (6!)
  Sydney:   P1-P5       (5) ──►   Spain:    —                   (0!)
  Spain:    —           (0)

  Paris absorbs ALL 6 partitions. Spain's tiny arc captures nothing.
```

This demonstrates the cascading failure problem: **without virtual nodes, all of a dead worker's load dumps onto a single clockwise neighbor**, rather than spreading across survivors.

**Step 5 — Sydney comes back. What happens?**

Re-add Sydney at position 66:

```
  P2 at 34 ──clockwise──► Sydney at 66    ← back to Sydney
  P3 at 47 ──clockwise──► Sydney at 66    ← back to Sydney
  (... all 5 partitions return to Sydney)
```

**Same hash, same position, same assignment.** This is the stickiness property — the assignment is a pure function of the ring state.

**Step 6 — New worker Tokyo joins.**

```
  SHA-256 mod 100:
  hash("tokyo") = 48       (SHA-256: afe04579...)
```

Ring: Spain(4) ── Tokyo(48) ── Sydney(66) ── Paris(99). Walk clockwise:

```
  P2 at 34 ──clockwise──► Tokyo at 48      ← was Sydney, now Tokyo
  P3 at 47 ──clockwise──► Tokyo at 48      ← was Sydney, now Tokyo
  P5 at 49 ──clockwise──► Sydney at 66     (still Sydney)
  P4 at 54 ──clockwise──► Sydney at 66     (still Sydney)
  P1 at 66 ──clockwise──► Sydney at 66     (still Sydney)
  P0 at 94 ──clockwise──► Paris at 99      (unchanged)
```

```
  Before:                          After Tokyo joins:
  ─────────                        ─────────────────
  Paris:    P0     (1)             Paris:    P0     (1)  (unchanged ✓)
  Sydney:   P1-P5  (5)            Sydney:   P1,P4,P5 (3) (lost P2,P3)
  Spain:    —      (0)            Tokyo:    P2,P3  (2)  (absorbed from Sydney)
                                   Spain:    —      (0)  (still nothing)

  Moved: P2, P3 (2 partitions)
  Stayed: P0, P1, P4, P5 (4 of 6 — untouched)
```

Tokyo absorbs partitions from the arc between itself and the previous worker (Spain). **Spain still gets nothing** because its arc (99→4) remains tiny regardless of how many workers join elsewhere.

**Why this matters for our use case:** The minimal-disruption property (only ~K/N keys move) is real and valuable, but the **distribution quality depends entirely on virtual nodes**. Without them, the ring is unreliable at small scale.

### The Virtual Node Solution

The example above demonstrated the real problem: 3 workers produced a 1-5-0 split. This isn't bad luck — with only 3 points on a ring of 100, the arcs are randomly sized and often wildly unequal.

**Solution: Virtual nodes.** Instead of placing each worker at 1 position, place them at multiple positions. Here are real SHA-256 mod 100 values for 3 virtual nodes per worker:

```
  hash("paris-0")  = 55    hash("paris-1")  = 16    hash("paris-2")  = 75
  hash("sydney-0") = 65    hash("sydney-1") = 29    hash("sydney-2") = 36
  hash("spain-0")  = 77    hash("spain-1")  = 51    hash("spain-2")  = 1

  Ring with 9 points (sorted):
  Pos: 1(ES) 16(PA) 29(SY) 36(SY) 51(ES) 55(PA) 65(SY) 75(PA) 77(ES)
       ──────────────────────────────────────────────────────────────────►
```

Now assign partitions:

```
  P0 at 94 ──clockwise──► spain-2 at 1    ✓ Spain owns P0
  P1 at 66 ──clockwise──► paris-2 at 75   ✓ Paris owns P1
  P2 at 34 ──clockwise──► sydney-2 at 36  ✓ Sydney owns P2
  P3 at 47 ──clockwise──► spain-1 at 51   ✓ Spain owns P3
  P4 at 54 ──clockwise──► paris-0 at 55   ✓ Paris owns P4
  P5 at 49 ──clockwise──► spain-1 at 51   ✓ Spain owns P5
```

```
  With 3 virtual nodes per worker:
  Paris:    P1, P4    (2 partitions)
  Sydney:   P2        (1 partition)
  Spain:    P0, P3, P5 (3 partitions)
```

Better than 1-5-0, but still not even (2-1-3). With 150 virtual nodes per worker, the distribution converges to nearly equal arcs and even partition counts. The trade-off: ring size grows to 450 entries.

**How partition assignment works with virtual nodes:** Same rule — walk clockwise, first virtual node you hit determines the owner. If you hit `sydney-2` (Sydney's 3rd virtual node at position 36), Sydney owns that partition. The "virtual" part is just for placement; ownership maps back to the real worker.

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

Consistent hashing is a foundational algorithm worth understanding, but for our partition assignment problem, **neither consistent hashing nor rendezvous hashing can guarantee even distribution when workers ≈ key groups**. At 128 KGs with 128 workers (1:1), both leave ~37% of workers idle — a mathematical certainty (balls-into-bins), not a tuning issue.

For **low worker counts** (3-10), rendezvous hashing (Option 6) is the practical choice among hash-based options — same minimal-disruption guarantee with less code, no tuning, and built-in weight support.

For **high worker counts** approaching the key group count, **only leader-based options (1, 2, 5) or claim-based (8) can guarantee exact 1:1 assignment**. Hash-based options need at least 8:1 KG-to-worker ratio (1024 KGs for 128 workers) to distribute reliably. This is the fundamental architectural constraint: the key group count must be chosen with both the hash-based distribution requirements AND the maximum worker count in mind.
