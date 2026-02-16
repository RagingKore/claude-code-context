# Actor Consensus Comparison

A side-by-side comparison of **multiple frameworks and approaches** implementing the same scenario: a 3-node cluster with leader election (or implicit leadership), leader failure, re-election, and long-running work simulation.

## Scenario

1. **3 nodes** start and form a cluster
2. **Leader election** — via Bully algorithm, Raft consensus, cluster singleton, or implicit oldest-member
3. All nodes execute **long-running subscription work** (simulated periodic processing)
4. The **leader is killed** mid-operation
5. Surviving nodes **detect failure** and **re-elect** (or rebalance)
6. Work **continues** on surviving nodes under the new leader

## Running

```bash
cd prototypes/actor-consensus-comparison
dotnet run --project src/ActorConsensus.Runner
```

The runner executes 5 implementations sequentially (KurrentDB-backed implementations require a live instance and are not included in the default runner).

## Implementations

### 1. Proto.Actor — Bully Election (in-process)

Custom Bully algorithm built on Proto.Actor's `IActor` interface. Each node is an actor that exchanges heartbeat and election messages. Highest alive node ID wins.

- **Project:** `ActorConsensus.ProtoActor`
- **Election:** Bully (manual heartbeat + timeout)
- **Transport:** In-process message passing
- **External deps:** None

### 2. Akka.NET — Bully Election (in-process)

Same Bully algorithm on Akka.NET's `ReceiveActor`. Demonstrates the `Receive<T>()` registration pattern vs Proto.Actor's single `ReceiveAsync` dispatch.

- **Project:** `ActorConsensus.AkkaDotNet`
- **Election:** Bully (manual heartbeat + timeout)
- **Transport:** In-process message passing
- **External deps:** None

### 3. Akka.NET — Cluster Singleton

Real Akka.Cluster with gossip-based membership and `ClusterSingletonManager` for automatic leader placement. No manual election — the framework handles it. Configured entirely through Akka.Hosting fluent API (no HOCON).

- **Project:** `ActorConsensus.AkkaCluster`
- **Election:** Cluster Singleton (gossip + automatic migration)
- **Transport:** TCP (Akka.Remote), 3 ActorSystems on ports 7551–7553
- **External deps:** None

### 4. Akka.NET — Gossip Partition Assignment (no election)

Demonstrates a fundamentally different approach: **no leader election at all**. Workers join a cluster, subscribe to membership changes, and each independently computes a deterministic partition map from the converged member list. The oldest worker is the "implicit leader." Fluent builder API: `Worker.Create(...).ListenOn(...).WithSeedNodes(...).WithPartitionCount(128).Build()`.

- **Project:** `ActorConsensus.AkkaWorker`
- **Election:** None — deterministic computation from gossip state
- **Transport:** TCP (Akka.Remote), 3 ActorSystems on ports 7661–7663
- **External deps:** None

### 5. dotNext Raft — TCP Consensus

Uses [dotNext.Net.Cluster](https://dotnet.github.io/dotNext/) Raft implementation with TCP transport and `ConsensusOnlyState` (no write-ahead log — pure leader election). Election is handled entirely by the Raft protocol with randomized timeouts.

- **Project:** `ActorConsensus.DotNextRaft`
- **Election:** Raft (term-based, randomized timeout 150–300ms)
- **Transport:** TCP, 3 RaftCluster instances on ports 7771–7773
- **External deps:** None

### 6. KurrentDB Membership — Plain C# (no actors)

Bully election using KurrentDB (EventStoreDB) as the sole communication channel. Each node writes `NodeHeartbeat` / `LeaderClaimed` / `NodeLeft` events to a shared stream. A catch-up subscription builds a local member view. No actor framework — just timers and subscriptions.

- **Project:** `ActorConsensus.KurrentDbMembership`
- **Election:** Bully via event stream (highest alive ID claims leadership)
- **Transport:** KurrentDB gRPC streams
- **External deps:** Requires live KurrentDB (`esdb://localhost:2113`)
- **Runner:** Not included in default runner (requires external infrastructure)

### 7. Akka.Discovery over KurrentDB (library)

Reusable `ServiceDiscovery` provider that uses KurrentDB heartbeat streams for Akka.Management Cluster Bootstrap. Not a standalone cluster implementation — designed to be plugged into Akka.Cluster setups where KurrentDB replaces multicast/DNS for node discovery.

- **Project:** `ActorConsensus.AkkaDiscoveryKurrentDb`
- **Role:** Discovery provider (library component)
- **External deps:** Requires live KurrentDB

## Project Structure

```
src/
├── ActorConsensus.Contracts/            # Shared types: IConsensusCluster, ClusterStatus, ConsensusLog
│
├── ActorConsensus.ProtoActor/           # [1] Bully election — Proto.Actor
├── ActorConsensus.AkkaDotNet/           # [2] Bully election — Akka.NET
├── ActorConsensus.AkkaCluster/          # [3] Cluster Singleton — Akka.NET
├── ActorConsensus.AkkaWorker/           # [4] Gossip partition assignment — Akka.NET
├── ActorConsensus.DotNextRaft/          # [5] Raft consensus — dotNext
├── ActorConsensus.KurrentDbMembership/  # [6] Bully over KurrentDB — plain C#
├── ActorConsensus.AkkaDiscoveryKurrentDb/ # [7] Discovery provider — Akka + KurrentDB
│
└── ActorConsensus.Runner/               # Console app running implementations [1]–[5]
```

## Comparison

| | Proto.Actor Bully | Akka.NET Bully | Akka Cluster Singleton | Akka Gossip Partitions | dotNext Raft | KurrentDB Membership |
|---|---|---|---|---|---|---|
| **Election** | Bully (manual) | Bully (manual) | Singleton (built-in) | None (implicit oldest) | Raft (built-in) | Bully (event stream) |
| **Failure detection** | Heartbeat + timeout | Heartbeat + timeout | Gossip protocol | Gossip protocol | Raft heartbeat | Heartbeat TTL |
| **Re-election time** | ~3s (configurable) | ~3s (configurable) | ~5s (gossip + migration) | ~3s (gossip convergence) | 150–300ms | ~2–5s (TTL + delay) |
| **Transport** | In-process | In-process | TCP (Akka.Remote) | TCP (Akka.Remote) | TCP | KurrentDB gRPC |
| **External infra** | None | None | None | None | None | KurrentDB |
| **Partition awareness** | No | No | No | Yes (128 partitions) | No | No |
| **Term tracking** | Pseudo-term | Pseudo-term | Pseudo-term | Pseudo-term | Native Raft term | Native term |

## Framework Comparison (Proto.Actor vs Akka.NET)

### Actor Definition

| Aspect | Proto.Actor | Akka.NET |
|--------|-------------|----------|
| **Base type** | Implement `IActor` interface | Inherit from `ReceiveActor` or `UntypedActor` |
| **Message dispatch** | Single `ReceiveAsync(IContext)` + pattern match | `Receive<T>(handler)` registration in constructor |
| **Lifecycle hooks** | Handle `Started`/`Stopping`/`Stopped` messages | Override `PreStart()`/`PostStop()` methods |
| **Actor creation** | `Props.FromProducer(() => new Actor())` | `Props.Create(() => new Actor())` |
| **Actor reference** | `PID` (Process ID) | `IActorRef` |
| **Fire-and-forget** | `context.Send(pid, message)` | `actorRef.Tell(message, sender)` |
| **Request-response** | `context.RequestAsync<T>(pid, msg)` | `actorRef.Ask<T>(msg, timeout)` |
| **Spawn** | `context.Spawn(props)` | `system.ActorOf(props, name)` |

### When to Use Which

| Use Case | Recommendation |
|----------|---------------|
| Lightweight, code-first microservices | Proto.Actor |
| Built-in cluster singleton/sharding | Akka.NET (Akka.Cluster.Tools) |
| Virtual actors (grain-style) | Proto.Actor (Proto.Cluster) |
| Existing Akka/JVM team experience | Akka.NET |
| Minimal dependencies | Proto.Actor |
| Complex supervision hierarchies | Akka.NET |
| gRPC-native remoting | Proto.Actor |
| Raft consensus without actors | dotNext.Net.Cluster |
| Event-sourced coordination | KurrentDB (EventStoreDB) |
| Deterministic partition assignment | Gossip + pure function (Akka.Cluster or custom) |

## Packages

- [Proto.Actor 1.8.0](https://www.nuget.org/packages/Proto.Actor)
- [Akka 1.5.59](https://www.nuget.org/packages/Akka) + Akka.Cluster, Akka.Remote, Akka.Cluster.Hosting, Akka.Discovery
- [DotNext.Net.Cluster 5.26.1](https://www.nuget.org/packages/DotNext.Net.Cluster)
- [EventStore.Client.Grpc.Streams 23.3.9](https://www.nuget.org/packages/EventStore.Client.Grpc.Streams)

## Design Document

See [DESIGN.md](DESIGN.md) for an in-depth analysis of 8 distributed partition assignment strategies, including key groups (virtual partitions), consistent hashing, rendezvous hashing, gossip+CRDT, and trade-off analysis.

## Target Framework

.NET 10 (`net10.0`) with C# preview language features.
