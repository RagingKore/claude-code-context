# Actor Consensus Comparison: Proto.Actor vs Akka.NET

A side-by-side comparison of **Proto.Actor** and **Akka.NET** implementing the same scenario: a 3-node cluster with Bully leader election, leader failure, re-election, and long-running subscription work simulation.

## Scenario

1. **3 nodes** start and form a cluster
2. **Leader election** via the [Bully algorithm](https://en.wikipedia.org/wiki/Bully_algorithm) — highest alive node ID wins
3. All nodes execute **long-running subscription work** (simulated periodic processing)
4. The **leader is killed** mid-operation
5. Surviving nodes **detect failure** via heartbeat timeout and **re-elect** a new leader
6. Work **continues uninterrupted** on surviving nodes under the new leader

## Running

```bash
cd prototypes/actor-consensus-comparison
dotnet run --project src/ActorConsensus.Runner
```

## Project Structure

```
src/
├── ActorConsensus.Contracts/     # Shared messages, interfaces, logging
│   ├── ConsensusMessages.cs      # All message types (election, work, lifecycle)
│   ├── IConsensusCluster.cs      # Common orchestrator interface
│   └── ConsensusLog.cs           # Colour-coded console logger
│
├── ActorConsensus.ProtoActor/    # Proto.Actor implementation
│   ├── ConsensusNodeActor.cs     # Node actor (IActor interface)
│   └── ProtoActorCluster.cs      # Cluster orchestrator
│
├── ActorConsensus.AkkaDotNet/    # Akka.NET implementation
│   ├── ConsensusNodeActor.cs     # Node actor (ReceiveActor base class)
│   └── AkkaCluster.cs            # Cluster orchestrator
│
└── ActorConsensus.Runner/        # Console app running both scenarios
    └── Program.cs
```

## Framework Comparison

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

### System Setup

| Aspect | Proto.Actor | Akka.NET |
|--------|-------------|----------|
| **System creation** | `new ActorSystem()` — zero config | `ActorSystem.Create(name)` — optional HOCON |
| **Configuration** | Fluent code-first | HOCON files or code |
| **Shutdown** | `system.ShutdownAsync()` | `system.Terminate()` |
| **Root context** | `system.Root` — used for top-level operations | System itself acts as guardian |

### Scheduling

| Aspect | Proto.Actor | Akka.NET |
|--------|-------------|----------|
| **Timer API** | Manual with `PeriodicTimer` / `Task.Delay` | `Context.System.Scheduler.ScheduleTellRepeatedlyCancelable()` |
| **One-shot** | `Task.Delay().ContinueWith()` | `Scheduler.ScheduleTellOnce()` |
| **Cancellation** | `CancellationTokenSource` | `ICancelable` return value |

### Peer Communication

| Aspect | Proto.Actor | Akka.NET |
|--------|-------------|----------|
| **Wiring peers** | Direct method call on actor instance | Send `RegisterPeer` message to actor |
| **Encapsulation** | Instance accessible from orchestrator | Actor internals hidden behind `IActorRef` |
| **Philosophy** | Pragmatic — allows hybrid access | Strict — everything through messages |

### Key Design Differences

**Proto.Actor** takes a more lightweight, interface-based approach:
- `IActor` is a single-method interface — total freedom in how you dispatch
- `IContext` is your window into the actor system — send messages, spawn children, access self
- No mandatory configuration — works out of the box
- Peer wiring can bypass the message system (direct method calls on the actor instance)
- Scheduling is manual — use standard .NET primitives (`PeriodicTimer`, `Task.Delay`)

**Akka.NET** follows the classic Akka model with more structure:
- `ReceiveActor` provides declarative message routing via `Receive<T>()` in the constructor
- `PreStart()`/`PostStop()` lifecycle hooks are method overrides, not messages
- Built-in scheduler with `ICancelable` handles for clean timer management
- Strict actor encapsulation — all interaction goes through `IActorRef` and messages
- `Sender` is implicitly available in message handlers for reply patterns
- HOCON configuration available for complex deployments

### When to Use Which

| Use Case | Recommendation |
|----------|---------------|
| Lightweight, code-first microservices | Proto.Actor |
| Need built-in cluster singleton/sharding | Akka.NET (Akka.Cluster.Tools) |
| Virtual actors (grain-style) | Proto.Actor (Proto.Cluster) |
| Existing Akka/JVM team experience | Akka.NET |
| Minimal dependencies | Proto.Actor |
| Complex supervision hierarchies | Akka.NET |
| gRPC-native remoting | Proto.Actor |
| TCP/QUIC remoting | Akka.NET (v1.6 roadmap) |

## Packages

- [Proto.Actor 1.8.0](https://www.nuget.org/packages/Proto.Actor)
- [Akka 1.5.59](https://www.nuget.org/packages/Akka)

## Target Framework

.NET 10 (`net10.0`) with C# preview language features.
