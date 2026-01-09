# gRPC Load Balancer Technical Specification

## Raging.Grpc.LoadBalancing

**Version:** 1.0.0
**Status:** Draft
**Last Updated:** 2026-01-09

---

## Table of Contents

1. [Overview](#1-overview)
2. [Goals & Non-Goals](#2-goals--non-goals)
3. [Architecture](#3-architecture)
4. [Component Design & Responsibilities](#4-component-design--responsibilities)
5. [Contracts & Interfaces](#5-contracts--interfaces)
6. [Configuration & API Reference](#6-configuration--api-reference)
7. [Sequence Diagrams](#7-sequence-diagrams)
8. [Design Decisions & Rationale](#8-design-decisions--rationale)
9. [Error Handling](#9-error-handling)
10. [Performance Considerations](#10-performance-considerations)
11. [File Structure](#11-file-structure)
12. [Examples](#12-examples)
13. [Internal Implementation Reference](#13-internal-implementation-reference)

---

## 1. Overview

A high-performance gRPC load balancer for .NET 8/9/10 that enables cluster-aware client-side load balancing through topology discovery.

### Core Principle

User implements **one interface** (`IPollingTopologySource` or `IStreamingTopologySource`), the library handles everything else:

- Topology discovery from seed nodes
- Node selection based on custom comparer (default: by Priority)
- Subchannel (connection) management
- Automatic refresh on failures
- Integration with gRPC's native load balancing infrastructure

### Key Features

- **Non-Generic API**: Simple `ClusterNode` record struct with `Metadata` dictionary for extensibility
- **Reactive Core**: Internal architecture is streaming-based; polling is adapted to streaming
- **Zero-Allocation Hot Path**: `Pick()` method allocates nothing
- **Flexible Configuration**: POCO options for JSON serialization + fluent builder
- **DI Support**: Full integration with `IServiceCollection`
- **Extensible**: Custom node selection order via `IComparer<ClusterNode>` on topology source
- **Separation of Concerns**: Each component has a single, well-defined responsibility

---

## 2. Goals & Non-Goals

### Goals

| Goal | Description |
|------|-------------|
| **Simplicity** | Single interface implementation, no generics needed |
| **Performance** | Zero allocations on pick hot path |
| **Flexibility** | Support polling and streaming topology sources |
| **Extensibility** | Metadata dictionary for custom node attributes |
| **Integration** | Seamless integration with standard `GrpcChannel` patterns |
| **Resilience** | Automatic retry with seed failover |
| **Observability** | Source-generated logging |
| **Separation of Concerns** | Each component does one thing well |

### Non-Goals (MVP)

| Non-Goal | Reason |
|----------|--------|
| IPv6 bracket notation | Complexity, defer to future |
| DNS SRV discovery | Defer to future |
| Weighted load balancing | Use Priority + custom comparer |
| Health checking | Rely on topology source for health info |

---

## 3. Architecture

### gRPC Load Balancing Concepts

In .NET gRPC, client-side load balancing consists of three components:

| Component | Responsibility |
|-----------|----------------|
| **Resolver** | Discovers server addresses (e.g., via DNS, service discovery) |
| **LoadBalancer** | Manages connections (subchannels) to those addresses |
| **SubchannelPicker** | Picks which connection to use for each RPC call |

```
┌─────────────────────────────────────────────────────────────────┐
│                         GrpcChannel                             │
│                                                                 │
│  ┌─────────────┐    ┌────────────────┐    ┌─────────────────┐  │
│  │   Resolver  │───▶│  LoadBalancer  │───▶│     Picker      │  │
│  │             │    │                │    │                 │  │
│  │ "Here are   │    │ "I manage      │    │ "Use THIS one   │  │
│  │  the        │    │  connections   │    │  for THIS       │  │
│  │  servers"   │    │  to them"      │    │  request"       │  │
│  └─────────────┘    └────────────────┘    └─────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### High-Level Architecture

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                              User Code                                        │
│                                                                               │
│  var channel = GrpcLoadBalancedChannel.ForAddress("node1:2113", lb => lb     │
│      .WithSeeds("node2:2113", "node3:2113")                                  │
│      .WithPollingTopologySource(new MyTopologySource()));                    │
│                                                                               │
│  var client = new MyService.MyServiceClient(channel);                        │
│  await client.DoSomethingAsync(request);  // ← Uses load balancing           │
└──────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                         Library Components                                    │
│                                                                               │
│  ┌────────────────────────────────────────────────────────────────────────┐  │
│  │                    Topology Source (User Implements)                    │  │
│  │                                                                         │  │
│  │  • IPollingTopologySource - request/response discovery                 │  │
│  │  • IStreamingTopologySource - server-push discovery                    │  │
│  │  • IComparer<ClusterNode> - node selection order (THE PICKING ALGO)    │  │
│  └────────────────────────────────────────────────────────────────────────┘  │
│                                    │                                          │
│                                    ▼                                          │
│  ┌────────────────────────────────────────────────────────────────────────┐  │
│  │                         ClusterResolver                                 │  │
│  │                                                                         │  │
│  │  • Subscribes to topology source                                       │  │
│  │  • Iterates through seeds sequentially on failure                      │  │
│  │  • Sorts nodes using topology source's IComparer<ClusterNode>          │  │
│  │  • Assigns order index to preserve comparer's sort order               │  │
│  │  • Passes addresses to LoadBalancer                                    │  │
│  └────────────────────────────────────────────────────────────────────────┘  │
│                                    │                                          │
│                                    ▼                                          │
│  ┌────────────────────────────────────────────────────────────────────────┐  │
│  │                       ClusterLoadBalancer                               │  │
│  │                                                                         │  │
│  │  • Creates/destroys subchannels (TCP connections)                      │  │
│  │  • Monitors subchannel health (Ready, Connecting, Idle, Failure)       │  │
│  │  • Passes ALL subchannels to Picker (no filtering, no sorting)         │  │
│  └────────────────────────────────────────────────────────────────────────┘  │
│                                    │                                          │
│                                    ▼                                          │
│  ┌────────────────────────────────────────────────────────────────────────┐  │
│  │                         ClusterPicker                                   │  │
│  │                                                                         │  │
│  │  • Filters to Ready subchannels only                                   │  │
│  │  • Sorts by order index (preserving topology source comparer order)    │  │
│  │  • Round-robins across sorted Ready subchannels                        │  │
│  │  • ZERO ALLOCATION on Pick() - called on every gRPC call               │  │
│  └────────────────────────────────────────────────────────────────────────┘  │
│                                                                               │
└──────────────────────────────────────────────────────────────────────────────┘
```

---

## 4. Component Design & Responsibilities

### Separation of Concerns

Each component has a single, well-defined responsibility:

| Component | Single Responsibility | Does NOT Do |
|-----------|----------------------|-------------|
| **TopologySource** | Discovers nodes, defines sort order | Connection management, picking |
| **PollingToStreamingAdapter** | Wraps polling source, retry with backoff | Discovery logic, connection management |
| **Resolver** | Converts topology → addresses (sorts using source's comparer) | Connection management |
| **LoadBalancer** | Manages connections (subchannels) | Filtering, sorting, picking |
| **Picker** | Selects connection for each request | Discovery, connection management |

### Component Details

#### 1. ClusterNode

**The single node type used throughout the library.**

```csharp
public readonly record struct ClusterNode {
    public required DnsEndPoint EndPoint { get; init; }
    public bool IsEligible { get; init; } = true;
    public int Priority { get; init; } = 0;
    public ImmutableDictionary<string, object> Metadata { get; init; } = ImmutableDictionary<string, object>.Empty;

    public T? GetMetadata<T>(string key);
    public ClusterNode WithMetadata(string key, object value);
}
```

**Key Points:**
- Concrete record struct, not an interface
- `Metadata` dictionary for custom attributes (datacenter, zone, version, etc.)
- No generics needed - one type fits all use cases
- Helper methods for metadata access

#### 2. IPollingTopologySource / IStreamingTopologySource

**Responsibility:** Discover cluster topology and define node selection order.

```csharp
public interface IStreamingTopologySource : IComparer<ClusterNode> {
    // Discover topology
    IAsyncEnumerable<ClusterTopology> SubscribeAsync(
        TopologyContext context,
        CancellationToken cancellationToken = default);

    // Define selection order (THE PICKING ALGORITHM)
    // Default: sort by Priority ascending
    int IComparer<ClusterNode>.Compare(ClusterNode x, ClusterNode y) =>
        x.Priority.CompareTo(y.Priority);
}
```

**Key Points:**
- User implements this interface
- `SubscribeAsync` yields topology snapshots (streaming) or single snapshot (polling via adapter)
- `IComparer<ClusterNode>` determines node selection order - **this IS the picking algorithm**
- Default comparer sorts by `Priority` ascending (lower = preferred)
- Custom comparers can use `Metadata` for domain-specific sorting (datacenter, latency, etc.)

#### 3. PollingToStreamingAdapter

**Responsibility:** Convert polling source to streaming interface with resilient retry.

```csharp
internal sealed class PollingToStreamingAdapter : IStreamingTopologySource
```

**Key Points:**
- Wraps `IPollingTopologySource`
- Polls at configured interval (`Delay`), yields each result
- On failure: exponential backoff retry (`InitialBackoff` → `MaxBackoff`)
- After `MaxDiscoveryAttempts` consecutive failures: let exception propagate (triggers seed failover)
- Success resets failure counter
- Internal architecture is always streaming-based

#### 4. ClusterResolver

**Responsibility:** Subscribe to topology, convert nodes to addresses with order index.

```csharp
internal sealed class ClusterResolver : Resolver, IAsyncDisposable
```

**Key Points:**
- Iterates through seeds sequentially (round-robin on failure)
- Stays connected to streaming source "forever" until it ends
- On topology update:
  1. Filters to eligible nodes
  2. Sorts using topology source (IComparer<ClusterNode>)
  3. Assigns sequential order index (0, 1, 2...)
  4. Passes `BalancerAddress` list to LoadBalancer
- **Does NOT manage connections**

#### 5. ClusterLoadBalancer

**Responsibility:** Manage subchannels (connections) to addresses.

```csharp
internal sealed class ClusterLoadBalancer : LoadBalancer
```

**Key Points:**
- Creates/destroys `Subchannel` objects for each address
- Monitors subchannel state changes
- On state change: creates new `ClusterPicker` with **ALL** subchannels
- **Does NOT filter or sort** - passes everything to Picker
- **Does NOT pick** - that's the Picker's job

#### 6. ClusterPicker

**Responsibility:** Select which subchannel to use for each gRPC call.

```csharp
internal sealed class ClusterPicker : SubchannelPicker
```

**Key Points:**
- Called on **every single gRPC call** (hot path)
- At construction:
  1. Filters to Ready subchannels only
  2. Sorts by order index (preserves topology source comparer order)
- At `Pick()`:
  1. Atomic increment of round-robin index
  2. Return `_readySubchannels[index % count]`
  3. **ZERO ALLOCATIONS**

#### 7. SeedChannelPool

**Responsibility:** Manage reusable gRPC channels to seed nodes.

```csharp
internal sealed class SeedChannelPool : IAsyncDisposable
```

**Key Points:**
- Creates channels lazily
- Reuses channels across discovery attempts
- Disposes all channels on shutdown

#### 8. ClusterTopology

**Responsibility:** Immutable snapshot of cluster nodes with cached computations.

```csharp
public readonly record struct ClusterTopology(ImmutableArray<ClusterNode> Nodes)
```

**Key Points:**
- Uses `ImmutableArray<ClusterNode>` for value semantics
- Caches hash code and eligible count at construction
- Value equality based on node content (not reference)
- `ComputeDiff()` method for change detection

---

## 5. Contracts & Interfaces

### ClusterNode

```csharp
/// <summary>
/// Represents a node in the cluster.
/// </summary>
public readonly record struct ClusterNode {
    /// <summary>
    /// The endpoint to connect to.
    /// </summary>
    public required DnsEndPoint EndPoint { get; init; }

    /// <summary>
    /// Whether this node can accept connections.
    /// Nodes with IsEligible=false are excluded from load balancing.
    /// </summary>
    public bool IsEligible { get; init; } = true;

    /// <summary>
    /// Selection priority. Lower values are preferred.
    /// Nodes with equal priority are load-balanced via round-robin.
    /// </summary>
    public int Priority { get; init; } = 0;

    /// <summary>
    /// Custom metadata for domain-specific node information.
    /// Use this for datacenter, zone, version, or any other custom attributes.
    /// </summary>
    public ImmutableDictionary<string, object> Metadata { get; init; }
        = ImmutableDictionary<string, object>.Empty;

    /// <summary>
    /// Gets a metadata value by key, or default if not found.
    /// </summary>
    public T? GetMetadata<T>(string key) =>
        Metadata.TryGetValue(key, out var value) && value is T typed ? typed : default;

    /// <summary>
    /// Creates a new node with additional metadata.
    /// </summary>
    public ClusterNode WithMetadata(string key, object value) =>
        this with { Metadata = Metadata.SetItem(key, value) };
}
```

### ClusterTopology

```csharp
/// <summary>
/// Immutable snapshot of cluster topology with cached computations.
/// </summary>
public readonly record struct ClusterTopology(ImmutableArray<ClusterNode> Nodes) {
    public static ClusterTopology Empty => new(ImmutableArray<ClusterNode>.Empty);
    public bool IsEmpty => Nodes.IsDefaultOrEmpty;
    public int Count => Nodes.IsDefaultOrEmpty ? 0 : Nodes.Length;
    public int EligibleCount { get; }  // Cached at construction

    // Value equality based on node content
    public bool Equals(ClusterTopology other);
    public override int GetHashCode();  // Cached at construction

    // Change detection
    public (int Added, int Removed) ComputeDiff(ClusterTopology other);
}
```

### TopologyContext

```csharp
/// <summary>
/// Context provided to topology source operations.
/// </summary>
public sealed record TopologyContext {
    /// <summary>
    /// Channel connected to a cluster seed node.
    /// </summary>
    public required ChannelBase Channel { get; init; }

    /// <summary>
    /// Cancellation token for the operation.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Timeout for this topology call.
    /// </summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>
    /// The endpoint this channel is connected to.
    /// </summary>
    public required DnsEndPoint Endpoint { get; init; }
}
```

### IPollingTopologySource

```csharp
/// <summary>
/// Polling topology source. Implement for request/response discovery protocols.
/// The library polls at the configured delay interval.
/// </summary>
public interface IPollingTopologySource : IComparer<ClusterNode> {
    /// <summary>
    /// Fetch current cluster topology.
    /// </summary>
    ValueTask<ClusterTopology> GetClusterAsync(TopologyContext context);

    /// <summary>
    /// Node comparison for selection order. THE PICKING ALGORITHM.
    /// Default: sort by Priority ascending (lower = preferred).
    /// Override to customize (e.g., prefer same datacenter, least latency).
    /// </summary>
    int IComparer<ClusterNode>.Compare(ClusterNode x, ClusterNode y) =>
        x.Priority.CompareTo(y.Priority);
}
```

### IStreamingTopologySource

```csharp
/// <summary>
/// Streaming topology source. Implement if server pushes topology changes.
/// </summary>
public interface IStreamingTopologySource : IComparer<ClusterNode> {
    /// <summary>
    /// Subscribe to cluster topology changes.
    /// Each yielded value is the complete current topology (snapshot model).
    /// The enumerable should yield continuously until cancelled.
    /// </summary>
    IAsyncEnumerable<ClusterTopology> SubscribeAsync(
        TopologyContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Node comparison for selection order. THE PICKING ALGORITHM.
    /// Default: sort by Priority ascending (lower = preferred).
    /// Override to customize (e.g., prefer same datacenter, least latency).
    /// </summary>
    int IComparer<ClusterNode>.Compare(ClusterNode x, ClusterNode y) =>
        x.Priority.CompareTo(y.Priority);
}
```

---

## 6. Configuration & API Reference

### 6.1 Entry Points

There are two ways to create a load-balanced channel:

#### GrpcLoadBalancedChannel (Static Factory - Non-DI)

```csharp
public static class GrpcLoadBalancedChannel {
    /// <summary>
    /// Create a load-balanced channel for the specified address.
    /// </summary>
    /// <param name="address">The primary endpoint address. Format: "host:port"</param>
    /// <param name="configure">Configuration delegate.</param>
    /// <returns>A configured GrpcChannel with load balancing.</returns>
    public static GrpcChannel ForAddress(string address, Action<LoadBalancingBuilder> configure);

    /// <summary>
    /// Create a load-balanced channel from configuration.
    /// Reads Seeds and Resilience options from IConfiguration, then calls configure delegate.
    /// </summary>
    /// <param name="configuration">Configuration section containing LoadBalancingOptions.</param>
    /// <param name="configure">Configuration delegate for topology source and other non-serializable options.</param>
    /// <returns>A configured GrpcChannel with load balancing.</returns>
    public static GrpcChannel FromConfiguration(
        IConfiguration configuration,
        Action<LoadBalancingBuilder> configure);
}
```

#### ServiceCollectionExtensions (DI)

```csharp
public static class ServiceCollectionExtensions {
    /// <summary>
    /// Add gRPC load balancing to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="address">The primary endpoint address. Format: "host:port"</param>
    /// <returns>A builder for configuring load balancing.</returns>
    public static LoadBalancingServiceBuilder AddGrpcLoadBalancing(
        this IServiceCollection services,
        string address);

    /// <summary>
    /// Add gRPC load balancing from configuration.
    /// Reads Seeds and Resilience options from IConfiguration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="address">The primary endpoint address. Format: "host:port"</param>
    /// <param name="configuration">Configuration section containing LoadBalancingOptions.</param>
    /// <returns>A builder for configuring load balancing.</returns>
    public static LoadBalancingServiceBuilder AddGrpcLoadBalancing(
        this IServiceCollection services,
        string address,
        IConfiguration configuration);
}
```

---

### 6.2 LoadBalancingBuilder (Non-DI)

Used with `GrpcLoadBalancedChannel.ForAddress()`. All methods return `this` for chaining.

```csharp
public sealed class LoadBalancingBuilder {

    // ═══════════════════════════════════════════════════════════════
    // SEEDS - Additional seed nodes for failover
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Add seeds as strings. Format: "host:port"
    /// </summary>
    public LoadBalancingBuilder WithSeeds(params string[] endpoints);

    /// <summary>
    /// Add seeds as DnsEndPoints.
    /// </summary>
    public LoadBalancingBuilder WithSeeds(params DnsEndPoint[] endpoints);

    /// <summary>
    /// Add seeds from enumerable.
    /// </summary>
    public LoadBalancingBuilder WithSeeds(IEnumerable<DnsEndPoint> endpoints);

    // ═══════════════════════════════════════════════════════════════
    // RESILIENCE - Timeout, backoff, retry configuration
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Configure resilience options (timeout, backoff, max attempts).
    /// </summary>
    public LoadBalancingBuilder WithResilience(Action<ResilienceOptions> configure);

    // ═══════════════════════════════════════════════════════════════
    // TOPOLOGY SOURCE - Required, choose one
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Use a polling topology source instance.
    /// </summary>
    /// <param name="source">The topology source instance.</param>
    /// <param name="delay">Delay between successful polls. Default: 30 seconds.</param>
    public LoadBalancingBuilder WithPollingTopologySource(
        IPollingTopologySource source,
        TimeSpan? delay = null);

    /// <summary>
    /// Use a streaming topology source instance.
    /// </summary>
    public LoadBalancingBuilder WithStreamingTopologySource(IStreamingTopologySource source);

    // ═══════════════════════════════════════════════════════════════
    // REFRESH POLICY - When to trigger topology refresh on errors
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Custom policy for triggering topology refresh on gRPC errors.
    /// Default: refresh on StatusCode.Unavailable.
    /// </summary>
    public LoadBalancingBuilder WithRefreshPolicy(ShouldRefreshTopology policy);

    // ═══════════════════════════════════════════════════════════════
    // LOGGING
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Configure logging. If not set, no logging occurs.
    /// </summary>
    public LoadBalancingBuilder WithLoggerFactory(ILoggerFactory loggerFactory);

    // ═══════════════════════════════════════════════════════════════
    // CHANNEL OPTIONS - Configure underlying GrpcChannel
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Configure the underlying GrpcChannel options.
    /// Called for both seed channels and the main channel.
    /// </summary>
    public LoadBalancingBuilder ConfigureChannel(Action<GrpcChannelOptions> configure);

    /// <summary>
    /// Use TLS for all connections (seeds and cluster nodes).
    /// Default: false (plain HTTP/2).
    /// </summary>
    public LoadBalancingBuilder UseTls(bool useTls = true);
}
```

---

### 6.3 LoadBalancingServiceBuilder (DI)

Used with `services.AddGrpcLoadBalancing()`. All methods return `this` for chaining.

```csharp
public sealed class LoadBalancingServiceBuilder {

    // ═══════════════════════════════════════════════════════════════
    // SEEDS - Additional seed nodes for failover
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Add seeds as strings. Format: "host:port"
    /// </summary>
    public LoadBalancingServiceBuilder WithSeeds(params string[] endpoints);

    /// <summary>
    /// Add seeds as DnsEndPoints.
    /// </summary>
    public LoadBalancingServiceBuilder WithSeeds(params DnsEndPoint[] endpoints);

    /// <summary>
    /// Add seeds from enumerable.
    /// </summary>
    public LoadBalancingServiceBuilder WithSeeds(IEnumerable<DnsEndPoint> endpoints);

    // ═══════════════════════════════════════════════════════════════
    // RESILIENCE - Timeout, backoff, retry configuration
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Configure resilience options (timeout, backoff, max attempts).
    /// </summary>
    public LoadBalancingServiceBuilder WithResilience(Action<ResilienceOptions> configure);

    // ═══════════════════════════════════════════════════════════════
    // POLLING TOPOLOGY SOURCE - Choose one source type
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Register topology source type for DI resolution.
    /// The type is registered as singleton and resolved from the service provider.
    /// </summary>
    /// <typeparam name="TSource">The topology source type.</typeparam>
    /// <param name="delay">Delay between successful polls. Default: 30 seconds.</param>
    public LoadBalancingServiceBuilder WithPollingTopologySource<TSource>(TimeSpan? delay = null)
        where TSource : class, IPollingTopologySource;

    /// <summary>
    /// Use a pre-created topology source instance.
    /// </summary>
    /// <param name="source">The topology source instance.</param>
    /// <param name="delay">Delay between successful polls. Default: 30 seconds.</param>
    public LoadBalancingServiceBuilder WithPollingTopologySource(
        IPollingTopologySource source,
        TimeSpan? delay = null);

    /// <summary>
    /// Use a factory function to create the topology source.
    /// The factory receives IServiceProvider for dependency resolution.
    /// </summary>
    /// <param name="factory">Factory function receiving IServiceProvider.</param>
    /// <param name="delay">Delay between successful polls. Default: 30 seconds.</param>
    public LoadBalancingServiceBuilder WithPollingTopologySource(
        Func<IServiceProvider, IPollingTopologySource> factory,
        TimeSpan? delay = null);

    // ═══════════════════════════════════════════════════════════════
    // STREAMING TOPOLOGY SOURCE - Choose one source type
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Register streaming topology source type for DI resolution.
    /// The type is registered as singleton and resolved from the service provider.
    /// </summary>
    /// <typeparam name="TSource">The topology source type.</typeparam>
    public LoadBalancingServiceBuilder WithStreamingTopologySource<TSource>()
        where TSource : class, IStreamingTopologySource;

    /// <summary>
    /// Use a pre-created streaming topology source instance.
    /// </summary>
    public LoadBalancingServiceBuilder WithStreamingTopologySource(IStreamingTopologySource source);

    /// <summary>
    /// Use a factory function to create the streaming topology source.
    /// The factory receives IServiceProvider for dependency resolution.
    /// </summary>
    public LoadBalancingServiceBuilder WithStreamingTopologySource(
        Func<IServiceProvider, IStreamingTopologySource> factory);

    // ═══════════════════════════════════════════════════════════════
    // CONFIGURATION - Refresh policy, channel options, TLS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Custom policy for triggering topology refresh on gRPC errors.
    /// Default: refresh on StatusCode.Unavailable.
    /// </summary>
    public LoadBalancingServiceBuilder WithRefreshPolicy(ShouldRefreshTopology policy);

    /// <summary>
    /// Configure the underlying GrpcChannel options.
    /// Called for both seed channels and the main channel.
    /// </summary>
    public LoadBalancingServiceBuilder ConfigureChannel(Action<GrpcChannelOptions> configure);

    /// <summary>
    /// Use TLS for all connections (seeds and cluster nodes).
    /// Default: false (plain HTTP/2).
    /// </summary>
    public LoadBalancingServiceBuilder UseTls(bool useTls = true);

    // ═══════════════════════════════════════════════════════════════
    // BUILD - Finalize and register in service collection
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Register the configured GrpcChannel as a singleton in the service collection.
    /// Must be called to complete configuration.
    /// </summary>
    /// <returns>The service collection for further chaining.</returns>
    public IServiceCollection Build();
}
```

---

### 6.4 Configuration Options (POCO)

These classes are JSON-serializable for configuration file support.

```csharp
public sealed class LoadBalancingOptions {
    /// <summary>
    /// Seed endpoints for discovery. Format: "host:port"
    /// First seed is used as primary endpoint.
    /// </summary>
    public required string[] Seeds { get; set; }

    /// <summary>
    /// Delay between topology polls (only for polling source).
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Resilience configuration.
    /// </summary>
    public ResilienceOptions Resilience { get; set; } = new();
}

public sealed class ResilienceOptions {
    /// <summary>
    /// Timeout for individual topology calls.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum consecutive failures before giving up on a seed.
    /// After this many failures, the adapter lets the exception propagate
    /// so the resolver can try the next seed.
    /// Default: 10.
    /// </summary>
    public int MaxDiscoveryAttempts { get; set; } = 10;

    /// <summary>
    /// Initial backoff duration after a polling failure.
    /// Grows exponentially: InitialBackoff * 2^(attempt-1).
    /// Default: 100 milliseconds.
    /// </summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Maximum backoff duration (cap for exponential growth).
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// gRPC status codes that trigger topology refresh.
    /// Default: [14] (Unavailable).
    /// </summary>
    public int[] RefreshOnStatusCodes { get; set; } = [14];
}
```

---

### 6.5 Refresh Policy

```csharp
/// <summary>
/// Delegate that determines if topology should be refreshed based on an RPC exception.
/// </summary>
public delegate bool ShouldRefreshTopology(RpcException exception);

/// <summary>
/// Built-in refresh policies for determining when to refresh topology.
/// </summary>
public static class RefreshPolicy {
    /// <summary>
    /// Refresh on Unavailable status (connection issues).
    /// This is the default policy.
    /// </summary>
    public static readonly ShouldRefreshTopology Default;

    /// <summary>
    /// Refresh on any of the specified status codes.
    /// </summary>
    public static ShouldRefreshTopology OnStatusCodes(params StatusCode[] codes);

    /// <summary>
    /// Create a policy from status code integers (useful for JSON configuration).
    /// </summary>
    public static ShouldRefreshTopology FromStatusCodeInts(params int[] statusCodes);

    /// <summary>
    /// Refresh when exception message contains any of the specified strings (case-insensitive).
    /// </summary>
    public static ShouldRefreshTopology OnMessageContains(params string[] triggers);

    /// <summary>
    /// Combine multiple policies (refresh if ANY match).
    /// </summary>
    public static ShouldRefreshTopology Any(params ShouldRefreshTopology[] policies);

    /// <summary>
    /// Combine multiple policies (refresh if ALL match).
    /// </summary>
    public static ShouldRefreshTopology All(params ShouldRefreshTopology[] policies);
}
```

---

### 6.6 JSON Configuration Example

```json
{
  "LoadBalancing": {
    "Seeds": ["node1:2113", "node2:2113", "node3:2113"],
    "Delay": "00:00:30",
    "Resilience": {
      "Timeout": "00:00:05",
      "MaxDiscoveryAttempts": 10,
      "InitialBackoff": "00:00:00.100",
      "MaxBackoff": "00:00:05",
      "RefreshOnStatusCodes": [14]
    }
  }
}
```

---

### 6.7 Usage Examples

#### Non-DI: Basic Usage

```csharp
var channel = GrpcLoadBalancedChannel.ForAddress("node1:5000", lb => lb
    .WithSeeds("node2:5000", "node3:5000")
    .WithPollingTopologySource(new MyTopologySource()));

var client = new MyService.MyServiceClient(channel);
```

#### Non-DI: Full Configuration

```csharp
var channel = GrpcLoadBalancedChannel.ForAddress("node1:5000", lb => lb
    .WithSeeds("node2:5000", "node3:5000")
    .WithPollingTopologySource(new MyTopologySource(), delay: TimeSpan.FromSeconds(15))
    .WithResilience(r => {
        r.Timeout = TimeSpan.FromSeconds(3);
        r.MaxDiscoveryAttempts = 5;
        r.InitialBackoff = TimeSpan.FromMilliseconds(200);
        r.MaxBackoff = TimeSpan.FromSeconds(10);
    })
    .WithRefreshPolicy(RefreshPolicy.Any(
        RefreshPolicy.Default,
        RefreshPolicy.OnStatusCodes(StatusCode.NotFound)))
    .WithLoggerFactory(loggerFactory)
    .ConfigureChannel(opts => {
        opts.MaxReceiveMessageSize = 16 * 1024 * 1024;
        opts.MaxSendMessageSize = 16 * 1024 * 1024;
    })
    .UseTls());
```

#### Non-DI: From Configuration File

```csharp
var config = configuration.GetSection("LoadBalancing");
var channel = GrpcLoadBalancedChannel.FromConfiguration(config, lb => lb
    .WithPollingTopologySource(new MyTopologySource())
    .WithLoggerFactory(loggerFactory));
```

#### DI: Type Registration

```csharp
// Register topology source type - resolved from DI
services.AddSingleton<MyTopologySource>();

services.AddGrpcLoadBalancing("node1:5000")
    .WithSeeds("node2:5000", "node3:5000")
    .WithPollingTopologySource<MyTopologySource>(delay: TimeSpan.FromSeconds(30))
    .Build();

// Inject
public class MyService(GrpcChannel channel) {
    readonly MyGrpcClient _client = new(channel);
}
```

#### DI: Factory Function

```csharp
services.AddGrpcLoadBalancing("node1:5000")
    .WithSeeds("node2:5000", "node3:5000")
    .WithPollingTopologySource(sp => {
        var config = sp.GetRequiredService<IOptions<MyConfig>>().Value;
        var logger = sp.GetRequiredService<ILogger<MyTopologySource>>();
        return new MyTopologySource(config.PreferredDatacenter, logger);
    }, delay: TimeSpan.FromSeconds(30))
    .WithResilience(r => r.Timeout = TimeSpan.FromSeconds(3))
    .ConfigureChannel(opts => opts.Credentials = ChannelCredentials.SecureSsl)
    .UseTls()
    .Build();
```

#### DI: From Configuration File

```csharp
var config = configuration.GetSection("LoadBalancing");

services.AddGrpcLoadBalancing("node1:5000", config)
    .WithPollingTopologySource<MyTopologySource>()
    .Build();
```

#### DI: Streaming Source

```csharp
services.AddGrpcLoadBalancing("node1:5000")
    .WithSeeds("node2:5000", "node3:5000")
    .WithStreamingTopologySource<MyStreamingSource>()
    .Build();
```

---

## 7. Sequence Diagrams

### Startup & Initial Discovery

```mermaid
sequenceDiagram
    participant User
    participant Channel as GrpcChannel
    participant Resolver as ClusterResolver
    participant Pool as SeedChannelPool
    participant Source as TopologySource
    participant Seed as Seed Node
    participant LB as LoadBalancer
    participant Picker as ClusterPicker

    User->>Channel: ForAddress("node1:2113", configure)

    Note over Resolver: OnStarted()
    Resolver->>Resolver: Start subscription loop

    loop Try seeds sequentially
        Resolver->>Pool: GetChannel(seed)
        Pool-->>Resolver: gRPC channel

        Resolver->>Source: SubscribeAsync(context)
        Source->>Seed: gRPC call (e.g., Gossip.Read)

        alt Success
            Seed-->>Source: Cluster info
            Source-->>Resolver: yield ClusterTopology
            Note over Resolver: Break loop, stay subscribed
        else Failure
            Seed--xSource: Error
            Source--xResolver: Exception
            Resolver->>Resolver: Log, try next seed
        end
    end

    Note over Resolver: Process topology
    Resolver->>Resolver: Filter eligible nodes
    Resolver->>Resolver: Sort using IComparer<ClusterNode>
    Resolver->>Resolver: Assign order index (0,1,2...)

    Resolver->>LB: UpdateChannelState(addresses)

    LB->>LB: Create subchannels
    LB->>Picker: new ClusterPicker(ALL subchannels)

    Note over Picker: Constructor
    Picker->>Picker: Filter Ready subchannels
    Picker->>Picker: Sort by order index

    Picker-->>LB: Picker ready
    LB-->>Channel: Ready
    Channel-->>User: GrpcChannel
```

### Per-Call Pick (Hot Path)

```mermaid
sequenceDiagram
    participant Client
    participant Channel as GrpcChannel
    participant LB as LoadBalancer
    participant Picker as ClusterPicker
    participant Sub as Subchannel
    participant Server

    Client->>Channel: client.DoSomethingAsync(request)
    Channel->>LB: Pick()
    LB->>Picker: Pick(context)

    Note over Picker: ZERO ALLOCATION
    Note over Picker: index = Interlocked.Increment(ref _index)
    Note over Picker: return _readySubchannels[index % count]

    Picker-->>LB: PickResult(subchannel)
    LB-->>Channel: Subchannel

    Channel->>Sub: Send request
    Sub->>Server: gRPC call
    Server-->>Sub: Response
    Sub-->>Channel: Response
    Channel-->>Client: Response
```

### Continuous Streaming Updates

```mermaid
sequenceDiagram
    participant Source as TopologySource
    participant Resolver as ClusterResolver
    participant LB as LoadBalancer
    participant Picker as ClusterPicker
    participant Server

    loop await foreach (topology in source.SubscribeAsync())
        Server-->>Source: Push topology update
        Source-->>Resolver: yield ClusterTopology

        alt Topology changed
            Resolver->>Resolver: Sort, assign order index
            Resolver->>LB: UpdateChannelState(addresses)
            LB->>LB: Add/remove subchannels
            LB->>Picker: new ClusterPicker(ALL subchannels)
            Note over Picker: Atomic picker swap
        else No change
            Note over Resolver: Skip update (same topology)
        end
    end

    Note over Resolver: Stream ended
    Resolver->>Resolver: Log, try next seed
```

### Seed Failover

```mermaid
sequenceDiagram
    participant Resolver as ClusterResolver
    participant Pool as SeedChannelPool
    participant Source as TopologySource
    participant Seed1
    participant Seed2
    participant Seed3

    Note over Resolver: seedIndex = 0

    Resolver->>Pool: GetChannel(Seed1)
    Resolver->>Source: SubscribeAsync(context)
    Source->>Seed1: gRPC call
    Seed1--xSource: Connection refused
    Source--xResolver: Exception
    Resolver->>Resolver: Log error, seedIndex = 1

    Resolver->>Pool: GetChannel(Seed2)
    Resolver->>Source: SubscribeAsync(context)
    Source->>Seed2: gRPC call
    Seed2--xSource: Timeout
    Source--xResolver: Exception
    Resolver->>Resolver: Log error, seedIndex = 2

    Resolver->>Pool: GetChannel(Seed3)
    Resolver->>Source: SubscribeAsync(context)
    Source->>Seed3: gRPC call
    Seed3-->>Source: Cluster info
    Source-->>Resolver: yield ClusterTopology

    Note over Resolver: Success! Stay connected to Seed3
```

### Reactive Refresh on Failure

```mermaid
sequenceDiagram
    participant Client
    participant Interceptor as RefreshInterceptor
    participant Resolver as ClusterResolver
    participant Source as TopologySource
    participant OldLeader
    participant NewLeader
    participant LB as LoadBalancer
    participant Picker as ClusterPicker

    Client->>Interceptor: DoSomethingAsync(request)
    Interceptor->>OldLeader: Forward call
    OldLeader--xInterceptor: RpcException(Unavailable)

    Interceptor->>Interceptor: ShouldRefresh? Yes
    Interceptor->>Resolver: Refresh()

    Note over Resolver: Cancel current subscription
    Note over Resolver: Start new subscription loop

    Resolver->>Source: SubscribeAsync(context)
    Source->>NewLeader: gRPC call
    NewLeader-->>Source: Updated topology
    Source-->>Resolver: yield ClusterTopology

    Resolver->>LB: UpdateChannelState(newAddresses)
    LB->>Picker: new ClusterPicker(subchannels)

    Interceptor-->>Client: RpcException (client retries)

    Note over Client: Retry with new picker
    Client->>Interceptor: DoSomethingAsync(request)
    Interceptor->>NewLeader: Forward call
    NewLeader-->>Client: Success
```

---

## 8. Design Decisions & Rationale

### Decision 1: Non-Generic API with Metadata Dictionary

**Choice:** Use concrete `ClusterNode` record struct with `Metadata` dictionary instead of generic `TNode`.

**Rationale:**
- **Simplicity**: Users don't need to define their own node type
- **No Generics**: Eliminates type parameter complexity throughout the codebase
- **Extensibility**: `Metadata` dictionary allows arbitrary custom attributes
- **Performance**: Hot path (`Pick()`) doesn't access metadata - only cold path (comparer)

**Implementation:**
```csharp
// User stores custom data in Metadata
var node = new ClusterNode {
    EndPoint = new DnsEndPoint("host", 5000),
    Priority = 1,
    Metadata = ImmutableDictionary<string, object>.Empty
        .Add("datacenter", "us-east-1")
        .Add("version", "2.0.0")
};

// Custom comparer accesses metadata
public int Compare(ClusterNode x, ClusterNode y) {
    var xDc = x.GetMetadata<string>("datacenter") ?? "";
    var yDc = y.GetMetadata<string>("datacenter") ?? "";
    // Prefer same datacenter
    var xLocal = xDc == _myDc ? 0 : 1;
    var yLocal = yDc == _myDc ? 0 : 1;
    if (xLocal != yLocal) return xLocal.CompareTo(yLocal);
    return x.Priority.CompareTo(y.Priority);
}
```

### Decision 2: Separation of Concerns

**Choice:** Each component (Resolver, LoadBalancer, Picker) has a single responsibility.

**Rationale:**
- **Testability**: Each component can be tested in isolation
- **Maintainability**: Changes to one concern don't affect others
- **Clarity**: Easy to understand what each component does

**Implementation:**
| Component | Does | Does NOT |
|-----------|------|----------|
| Resolver | Discovers addresses | Manage connections, pick |
| LoadBalancer | Manages connections | Filter, sort, pick |
| Picker | Picks connection per request | Discover, manage connections |

### Decision 3: Comparer on Topology Source

**Choice:** The `IComparer<ClusterNode>` is defined on the topology source interface.

**Rationale:**
- The topology source knows the domain (node types, priorities, datacenters)
- Custom sorting logic can use domain-specific fields in `Metadata`
- Single place to define "which nodes are preferred"

**Implementation:**
```csharp
// Default: sort by Priority
int IComparer<ClusterNode>.Compare(ClusterNode x, ClusterNode y) =>
    x.Priority.CompareTo(y.Priority);

// Custom: prefer same datacenter
public int Compare(ClusterNode x, ClusterNode y) {
    var xLocal = x.GetMetadata<string>("datacenter") == _myDc ? 0 : 1;
    var yLocal = y.GetMetadata<string>("datacenter") == _myDc ? 0 : 1;
    return xLocal != yLocal
        ? xLocal.CompareTo(yLocal)
        : x.Priority.CompareTo(y.Priority);
}
```

### Decision 4: Order Index Instead of Direct Comparer in Picker

**Choice:** Resolver sorts and assigns order index; Picker sorts by index.

**Rationale:**
- Picker only has `Subchannel` objects, not `ClusterNode` objects
- Order index preserves comparer's sort order across the boundary
- Simple integer comparison in Picker (fast)

**Implementation:**
```csharp
// In Resolver
eligible.Sort(_topologySource);  // Uses IComparer<ClusterNode>
for (var i = 0; i < eligible.Count; i++) {
    attributes.Add(OrderIndexKey, i);  // Preserve order
}

// In Picker
ready.Sort((a, b) => GetOrderIndex(a).CompareTo(GetOrderIndex(b)));
```

### Decision 5: LoadBalancer Passes ALL Subchannels

**Choice:** LoadBalancer passes all subchannels to Picker, not just Ready ones.

**Rationale:**
- **Separation of concerns**: LoadBalancer manages connections, Picker decides what's usable
- **Flexibility**: Picker could implement more sophisticated logic (e.g., prefer Connecting over none)
- **Immutability**: New Picker is created on state change; it sees snapshot of all subchannels

### Decision 6: Sequential Seed Iteration (Not Parallel)

**Choice:** On failure, try seeds one at a time in round-robin order.

**Rationale:**
- **Simplicity**: Easy to understand and debug
- **Efficiency**: No need to cancel parallel tasks
- **Streaming model**: Stay connected to one seed's stream; only switch on failure

### Decision 7: ClusterTopology as Value Object

**Choice:** `ClusterTopology` is a record struct with value equality.

**Rationale:**
- **Comparison**: Can check `if (oldTopology != newTopology)` efficiently
- **Caching**: Hash code and eligible count computed once at construction
- **Immutability**: Safe to pass around, no defensive copies needed

**Implementation:**
```csharp
public readonly record struct ClusterTopology(ImmutableArray<ClusterNode> Nodes) {
    readonly int _cachedHashCode;
    readonly int _eligibleCount;

    public ClusterTopology(ImmutableArray<ClusterNode> nodes) : this() {
        Nodes = nodes;
        _cachedHashCode = ComputeHashCode(nodes);
        _eligibleCount = CountEligible(nodes);
    }
}
```

### Decision 8: Streaming-First Internal Architecture

**Choice:** Everything internally is streaming-based; polling is adapted.

**Rationale:**
- **Reactive**: Immediately process topology updates as they arrive
- **Unified**: One code path for both polling and streaming sources
- **Natural**: `await foreach` fits perfectly

**Implementation:**
```csharp
// Resolver just iterates
await foreach (var topology in _topologySource.SubscribeAsync(context, ct)) {
    // Process topology update
}

// Polling adapter yields periodically
while (!ct.IsCancellationRequested) {
    yield return await _pollingSource.GetClusterAsync(context);
    await Task.Delay(_delay, ct);
}
```

### Decision 9: Backoff in PollingToStreamingAdapter (Not Resolver)

**Choice:** Exponential backoff retry is handled in `PollingToStreamingAdapter`, not in `ClusterResolver`.

**Rationale:**
- **Layered resilience**: Adapter handles transient failures within ONE seed; Resolver handles seed failover
- **Separation of concerns**: Adapter knows about polling errors; Resolver knows about seed switching
- **Configurable per-seed**: `MaxDiscoveryAttempts` failures before giving up on a seed

**Implementation:**
```csharp
// In PollingToStreamingAdapter
while (!ct.IsCancellationRequested) {
    try {
        topology = await _pollingSource.GetClusterAsync(context);
        consecutiveFailures = 0;  // Reset on success
    }
    catch (Exception ex) {
        consecutiveFailures++;

        if (consecutiveFailures >= _resilience.MaxDiscoveryAttempts)
            throw;  // Let resolver try next seed

        var backoff = BackoffCalculator.Calculate(
            consecutiveFailures,
            _resilience.InitialBackoff,
            _resilience.MaxBackoff);

        await Task.Delay(backoff, ct);
        continue;
    }
    yield return topology;
    await Task.Delay(_delay, ct);
}
```

**Backoff formula:** `min(InitialBackoff * 2^(attempt-1), MaxBackoff) ± 10% jitter`

---

## 9. Error Handling

### Exception Hierarchy

```csharp
public abstract class LoadBalancingException : Exception;

public sealed class LoadBalancingConfigurationException : LoadBalancingException;
public sealed class ClusterDiscoveryException : LoadBalancingException;
public sealed class NoEligibleNodesException : LoadBalancingException;
public sealed class TopologyException : LoadBalancingException;
```

### Failure Scenarios

| Scenario | Behavior |
|----------|----------|
| Polling fails (transient) | Exponential backoff retry (InitialBackoff → MaxBackoff) |
| Polling fails (MaxDiscoveryAttempts reached) | Let exception propagate, try next seed |
| Seed connection fails | Log, try next seed |
| Stream ends without data | Log `TopologyStreamEmpty`, try next seed |
| All seeds fail continuously | Keep trying (round-robin through seeds forever) |
| Topology returns no eligible nodes | Throw `NoEligibleNodesException` |
| Topology returns empty | Throw `ClusterDiscoveryException` |
| gRPC call fails with Unavailable | Trigger refresh via interceptor |

### Refresh Policy

```csharp
public delegate bool ShouldRefreshTopology(RpcException exception);

public static class RefreshPolicy {
    // Default: refresh on Unavailable status
    public static readonly ShouldRefreshTopology Default =
        static ex => ex.StatusCode == StatusCode.Unavailable;

    // Refresh on specific status codes
    public static ShouldRefreshTopology OnStatusCodes(params StatusCode[] codes);

    // Refresh on status codes as integers (for configuration)
    public static ShouldRefreshTopology FromStatusCodeInts(params int[] statusCodes);

    // Refresh when exception message contains specified strings
    public static ShouldRefreshTopology OnMessageContains(params string[] triggers);

    // Combine policies (refresh if ANY match)
    public static ShouldRefreshTopology Any(params ShouldRefreshTopology[] policies);

    // Combine policies (refresh if ALL match)
    public static ShouldRefreshTopology All(params ShouldRefreshTopology[] policies);
}
```

---

## 10. Performance Considerations

### Allocation Budgets

| Path | Allocation Budget | Frequency |
|------|-------------------|-----------|
| `Pick()` | **0 bytes** | Every gRPC call (millions/sec) |
| Topology update | < 4 KB | Every few seconds |
| Picker creation | < 1 KB | On topology change |
| Startup | Unbounded | Once |

### Hot Path: Pick()

```csharp
public override PickResult Pick(PickContext context) {
    // ZERO ALLOCATION - called on every gRPC call

    if (_readySubchannels.Length == 0)
        return PickResult.ForFailure(NoReadyNodesStatus);  // Static instance

    var index = Interlocked.Increment(ref _roundRobinIndex);
    var count = _readySubchannels.Length;
    var selectedIndex = ((index % count) + count) % count;

    return PickResult.ForSubchannel(_readySubchannels[selectedIndex]);
}
```

### Cold Path: Picker Construction

```csharp
public ClusterPicker(IReadOnlyList<Subchannel> subchannels) {
    // COLD PATH - only on topology change

    var ready = new List<Subchannel>();  // Allocation OK here
    foreach (var s in subchannels)
        if (s.State == ConnectivityState.Ready)
            ready.Add(s);

    ready.Sort((a, b) => GetOrderIndex(a).CompareTo(GetOrderIndex(b)));
    _readySubchannels = [.. ready];
}
```

### Metadata Performance

The `Metadata` dictionary is only accessed in:
- Topology source constructor (startup, cold)
- Custom comparer (topology update, cold)

**Never accessed on hot path** - so dictionary lookup overhead doesn't affect performance.

### ConfigureAwait(false)

All async methods use `ConfigureAwait(false)`:

```csharp
await _topologySource.SubscribeAsync(context, ct).ConfigureAwait(false);
await Task.Delay(delay, ct).ConfigureAwait(false);
```

---

## 11. File Structure

```
src/Raging.Grpc.LoadBalancing/
│
├── Raging.Grpc.LoadBalancing.csproj
├── GrpcLoadBalancedChannel.cs              # Static entry point
├── LoadBalancingBuilder.cs                 # Non-DI builder
│
├── Abstractions/
│   ├── ClusterNode.cs                      # Concrete node type with Metadata
│   ├── ClusterTopology.cs                  # Topology value object
│   ├── IPollingTopologySource.cs           # Polling source interface
│   ├── IStreamingTopologySource.cs         # Streaming source interface
│   └── TopologyContext.cs                  # Context record
│
├── Configuration/
│   ├── LoadBalancingOptions.cs             # POCO options
│   ├── ResilienceOptions.cs                # Resilience config
│   ├── ShouldRefreshTopology.cs            # Delegate
│   └── RefreshPolicy.cs                    # Built-in policies
│
├── Exceptions/
│   ├── LoadBalancingException.cs           # Base exception
│   ├── LoadBalancingConfigurationException.cs
│   ├── ClusterDiscoveryException.cs
│   ├── NoEligibleNodesException.cs
│   └── TopologyException.cs
│
├── Internal/
│   ├── ClusterResolver.cs                  # Topology → addresses
│   ├── ClusterResolverFactory.cs
│   ├── ClusterLoadBalancer.cs              # Manages subchannels
│   ├── ClusterLoadBalancerFactory.cs
│   ├── ClusterPicker.cs                    # Picks per request
│   ├── PollingToStreamingAdapter.cs        # Wraps polling source
│   ├── RefreshTriggerInterceptor.cs        # Triggers refresh on error
│   ├── SeedChannelPool.cs                  # Reusable seed channels
│   ├── BackoffCalculator.cs                # Exponential backoff
│   └── Log.cs                              # Source-generated logging
│
├── Utilities/
│   └── EndpointParser.cs                   # "host:port" parsing
│
└── Extensions/
    ├── ServiceCollectionExtensions.cs      # DI registration
    └── LoadBalancingServiceBuilder.cs      # DI builder
```

---

## 12. Examples

### Example 1: Simple Polling Source

```csharp
// Implement topology source (uses default comparer - by Priority)
public sealed class MyTopologySource : IPollingTopologySource {
    public async ValueTask<ClusterTopology> GetClusterAsync(TopologyContext context) {
        var client = new ClusterService.ClusterServiceClient(context.Channel);

        var response = await client.GetNodesAsync(
            new GetNodesRequest(),
            cancellationToken: context.CancellationToken);

        var nodes = response.Nodes
            .Select(n => new ClusterNode {
                EndPoint = new DnsEndPoint(n.Host, n.Port),
                IsEligible = n.Status == NodeStatus.Ready,
                Priority = n.IsLeader ? 0 : 1
            })
            .ToImmutableArray();

        return new ClusterTopology(nodes);
    }
}

// Use it
var channel = GrpcLoadBalancedChannel.ForAddress("node1:5000", lb => lb
    .WithSeeds("node2:5000", "node3:5000")
    .WithPollingTopologySource(new MyTopologySource(), delay: TimeSpan.FromSeconds(30)));

var client = new MyService.MyServiceClient(channel);
await client.DoSomethingAsync(request);
```

### Example 2: Custom Comparer (Datacenter Affinity)

```csharp
public sealed class DatacenterAwareSource : IPollingTopologySource {
    readonly string _preferredDc;

    public DatacenterAwareSource(string preferredDc) => _preferredDc = preferredDc;

    public async ValueTask<ClusterTopology> GetClusterAsync(TopologyContext context) {
        var client = new ClusterService.ClusterServiceClient(context.Channel);
        var response = await client.GetNodesAsync(new GetNodesRequest(),
            cancellationToken: context.CancellationToken);

        var nodes = response.Nodes
            .Select(n => new ClusterNode {
                EndPoint = new DnsEndPoint(n.Host, n.Port),
                IsEligible = n.Status == NodeStatus.Ready,
                Priority = n.IsLeader ? 0 : 1,
                Metadata = ImmutableDictionary<string, object>.Empty
                    .Add("datacenter", n.Datacenter)
            })
            .ToImmutableArray();

        return new ClusterTopology(nodes);
    }

    // Custom comparer: prefer same datacenter, then by priority
    public int Compare(ClusterNode x, ClusterNode y) {
        var xDc = x.GetMetadata<string>("datacenter") ?? "";
        var yDc = y.GetMetadata<string>("datacenter") ?? "";

        var xLocal = xDc == _preferredDc ? 0 : 1;
        var yLocal = yDc == _preferredDc ? 0 : 1;

        if (xLocal != yLocal)
            return xLocal.CompareTo(yLocal);

        return x.Priority.CompareTo(y.Priority);
    }
}

// Use it - nodes in same DC are preferred
var channel = GrpcLoadBalancedChannel.ForAddress("node1:5000", lb => lb
    .WithSeeds("node2:5000", "node3:5000")
    .WithPollingTopologySource(new DatacenterAwareSource("us-east-1")));
```

### Example 3: Streaming Source (Server Push)

```csharp
public sealed class StreamingTopologySource : IStreamingTopologySource {
    public async IAsyncEnumerable<ClusterTopology> SubscribeAsync(
        TopologyContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {

        var client = new ClusterService.ClusterServiceClient(context.Channel);

        using var stream = client.WatchCluster(
            new WatchRequest(),
            cancellationToken: cancellationToken);

        await foreach (var update in stream.ResponseStream.ReadAllAsync(cancellationToken)) {
            var nodes = update.Nodes
                .Select(n => new ClusterNode {
                    EndPoint = new DnsEndPoint(n.Host, n.Port),
                    IsEligible = n.Status == NodeStatus.Ready,
                    Priority = n.IsLeader ? 0 : 1
                })
                .ToImmutableArray();

            yield return new ClusterTopology(nodes);
        }
    }
}

// Use it - topology updates are pushed by server
var channel = GrpcLoadBalancedChannel.ForAddress("node1:5000", lb => lb
    .WithSeeds("node2:5000", "node3:5000")
    .WithStreamingTopologySource(new StreamingTopologySource()));
```

### Example 4: DI Registration with Factory

```csharp
// Register with factory for DI
services.AddGrpcLoadBalancing("node1:5000")
    .WithSeeds("node2:5000", "node3:5000")
    .WithPollingTopologySource(sp => {
        var config = sp.GetRequiredService<IOptions<MyConfig>>().Value;
        var logger = sp.GetRequiredService<ILogger<MyTopologySource>>();
        return new DatacenterAwareSource(config.PreferredDatacenter);
    })
    .ConfigureChannel(opts => {
        opts.Credentials = ChannelCredentials.SecureSsl;
    })
    .Build();

// Inject and use
public class MyService {
    readonly MyGrpcClient _client;

    public MyService(GrpcChannel channel) {
        _client = new MyGrpcClient(channel);
    }

    public Task DoWorkAsync() => _client.DoSomethingAsync(new Request());
}
```

### Example 5: Custom Refresh Policy

```csharp
var channel = GrpcLoadBalancedChannel.ForAddress("node1:5000", lb => lb
    .WithSeeds("node2:5000", "node3:5000")
    .WithPollingTopologySource(new MyTopologySource())
    .WithRefreshPolicy(RefreshPolicy.Any(
        RefreshPolicy.Default,  // Unavailable
        RefreshPolicy.OnStatusCodes(StatusCode.NotFound, StatusCode.Internal),
        ex => ex.Message.Contains("leader changed", StringComparison.OrdinalIgnoreCase)
    )));
```

---

## 13. Internal Implementation Reference

This section documents all internal components needed to implement the library from scratch.

### 13.1 EndpointParser (Utility)

```csharp
namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Utility for parsing endpoint strings.
/// </summary>
public static class EndpointParser {
    /// <summary>
    /// Parse a single endpoint string.
    /// </summary>
    /// <param name="input">The endpoint string in "host:port" format.</param>
    /// <returns>A DnsEndPoint representing the endpoint.</returns>
    /// <exception cref="LoadBalancingConfigurationException">Thrown when the format is invalid.</exception>
    public static DnsEndPoint Parse(string input) {
        var trimmed = input.Trim();
        var colonIndex = trimmed.LastIndexOf(':');

        if (colonIndex <= 0 || colonIndex == trimmed.Length - 1)
            throw new LoadBalancingConfigurationException(
                $"Invalid endpoint format: '{input}'. Expected 'host:port'.");

        var host = trimmed[..colonIndex];
        var portStr = trimmed[(colonIndex + 1)..];

        if (!int.TryParse(portStr, out var port) || port is <= 0 or > 65535)
            throw new LoadBalancingConfigurationException(
                $"Invalid port in endpoint: '{input}'.");

        return new DnsEndPoint(host, port);
    }
}
```

---

### 13.2 BackoffCalculator

```csharp
namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Calculates exponential backoff with jitter.
/// </summary>
internal static class BackoffCalculator {
    /// <summary>
    /// Calculate the backoff duration for a given attempt.
    /// Formula: min(initial * 2^(attempt-1), max) ± 10% jitter
    /// </summary>
    /// <param name="attempt">The current attempt number (1-based).</param>
    /// <param name="initial">The initial backoff duration.</param>
    /// <param name="max">The maximum backoff duration.</param>
    /// <returns>The calculated backoff duration with jitter.</returns>
    public static TimeSpan Calculate(int attempt, TimeSpan initial, TimeSpan max) {
        // Exponential: initial * 2^(attempt-1)
        // Cap the exponent to avoid overflow
        var exponent = Math.Min(attempt - 1, 30);
        var exponentialTicks = initial.Ticks * (1L << exponent);

        // Cap at max
        var cappedTicks = Math.Min(exponentialTicks, max.Ticks);

        // Add jitter: ±10%
        var jitter = Random.Shared.NextDouble() * 0.2 - 0.1;
        var jitteredTicks = (long)(cappedTicks * (1.0 + jitter));

        // Ensure non-negative
        return TimeSpan.FromTicks(Math.Max(0, jitteredTicks));
    }
}
```

---

### 13.3 SeedChannelPool

```csharp
namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Manages a pool of channels to seed endpoints for topology discovery.
/// Channels are created lazily and reused across discovery attempts.
/// </summary>
internal sealed class SeedChannelPool : IAsyncDisposable {
    readonly ConcurrentDictionary<DnsEndPoint, GrpcChannel> _channels = new();
    readonly GrpcChannelOptions? _channelOptions;
    readonly ILogger _logger;
    readonly bool _useTls;
    bool _disposed;

    /// <summary>
    /// Creates a new seed channel pool.
    /// </summary>
    /// <param name="channelOptions">Options for created channels.</param>
    /// <param name="useTls">Whether to use TLS (https) or plain HTTP/2 (http).</param>
    /// <param name="logger">Logger instance.</param>
    public SeedChannelPool(GrpcChannelOptions? channelOptions, bool useTls, ILogger logger);

    /// <summary>
    /// Gets or creates a channel to the specified endpoint.
    /// Thread-safe, channels are cached and reused.
    /// </summary>
    /// <param name="endpoint">The endpoint to connect to.</param>
    /// <returns>A GrpcChannel connected to the endpoint.</returns>
    public GrpcChannel GetChannel(DnsEndPoint endpoint);

    /// <summary>
    /// Disposes all cached channels.
    /// </summary>
    public ValueTask DisposeAsync();
}
```

---

### 13.4 PollingToStreamingAdapter

```csharp
namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Adapts a polling topology source to a streaming interface.
/// Implements retry with exponential backoff on transient failures.
/// </summary>
internal sealed class PollingToStreamingAdapter : IStreamingTopologySource {
    readonly IPollingTopologySource _pollingSource;
    readonly TimeSpan _delay;
    readonly ResilienceOptions _resilience;
    readonly ILogger? _logger;

    /// <summary>
    /// Creates a new adapter.
    /// </summary>
    /// <param name="pollingSource">The polling source to wrap.</param>
    /// <param name="delay">Delay between successful polls.</param>
    /// <param name="resilience">Resilience options for backoff configuration.</param>
    /// <param name="logger">Optional logger.</param>
    public PollingToStreamingAdapter(
        IPollingTopologySource pollingSource,
        TimeSpan delay,
        ResilienceOptions resilience,
        ILogger? logger = null);

    /// <summary>
    /// Subscribe to topology changes.
    /// Polls at configured interval, retries with backoff on failure.
    /// After MaxDiscoveryAttempts consecutive failures, throws to trigger seed failover.
    /// </summary>
    public IAsyncEnumerable<ClusterTopology> SubscribeAsync(
        TopologyContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delegates comparison to the wrapped polling source.
    /// </summary>
    public int Compare(ClusterNode x, ClusterNode y);
}
```

---

### 13.5 ClusterResolver

```csharp
namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Custom resolver that discovers cluster topology and reports addresses to the load balancer.
/// Iterates through seeds sequentially on failure. Stays connected to streaming source until it ends.
/// </summary>
internal sealed class ClusterResolver : Resolver, IAsyncDisposable {
    readonly IStreamingTopologySource _topologySource;
    readonly ImmutableArray<DnsEndPoint> _seeds;
    readonly SeedChannelPool _channelPool;
    readonly ResilienceOptions _resilience;
    readonly ILogger _logger;

    CancellationTokenSource? _cts;
    Task? _subscriptionTask;
    ClusterTopology _lastTopology;

    /// <summary>
    /// Creates a new cluster resolver.
    /// </summary>
    public ClusterResolver(
        IStreamingTopologySource topologySource,
        ImmutableArray<DnsEndPoint> seeds,
        SeedChannelPool channelPool,
        ResilienceOptions resilience,
        ILogger logger);

    /// <summary>
    /// Called when the resolver is started. Begins the subscription loop.
    /// </summary>
    protected override void OnStarted();

    /// <summary>
    /// Refresh topology by cancelling current subscription and restarting.
    /// Called by RefreshTriggerInterceptor when errors occur.
    /// </summary>
    public override void Refresh();

    /// <summary>
    /// Disposes the resolver and cancels any ongoing subscription.
    /// </summary>
    public ValueTask DisposeAsync();

    // Internal: SubscribeLoopAsync - round-robin through seeds forever
    // Internal: SubscribeToSeedAsync - subscribe to one seed, await foreach
    // Internal: ValidateTopology - throws if empty or no eligible nodes
    // Internal: BuildAddresses - sorts nodes, assigns order index, converts to BalancerAddress
}
```

**Key Implementation Details:**

```csharp
// BuildAddresses: How the comparer order is preserved
IReadOnlyList<BalancerAddress> BuildAddresses(ClusterTopology topology) {
    // Filter eligible nodes
    var eligible = new List<ClusterNode>();
    foreach (var node in topology.Nodes)
        if (node.IsEligible)
            eligible.Add(node);

    // Sort using topology source comparer - THIS IS THE PICKING ALGORITHM
    eligible.Sort(_topologySource);

    // Convert to addresses with order index from comparer
    var addresses = new List<BalancerAddress>(eligible.Count);
    for (var i = 0; i < eligible.Count; i++) {
        var node = eligible[i];
        var attributes = new BalancerAttributes {
            { ClusterPicker.OrderIndexKey, i }  // Preserve comparer order
        };
        addresses.Add(new BalancerAddress(node.EndPoint.Host, node.EndPoint.Port, attributes));
    }
    return addresses;
}
```

---

### 13.6 ClusterResolverFactory

```csharp
namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Factory for creating cluster resolvers.
/// Registered with gRPC's service provider to handle the "cluster" scheme.
/// </summary>
internal sealed class ClusterResolverFactory : ResolverFactory {
    /// <summary>
    /// The URI scheme this factory handles.
    /// </summary>
    public const string SchemeName = "cluster";

    readonly IStreamingTopologySource _topologySource;
    readonly ImmutableArray<DnsEndPoint> _seeds;
    readonly ResilienceOptions _resilience;
    readonly SeedChannelPool _channelPool;
    readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Creates a new resolver factory.
    /// </summary>
    public ClusterResolverFactory(
        IStreamingTopologySource topologySource,
        ImmutableArray<DnsEndPoint> seeds,
        ResilienceOptions resilience,
        SeedChannelPool channelPool,
        ILoggerFactory? loggerFactory);

    /// <inheritdoc />
    public override string Name => SchemeName;

    /// <inheritdoc />
    public override Resolver Create(ResolverOptions options);
}
```

---

### 13.7 ClusterLoadBalancer

```csharp
namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Custom load balancer that manages subchannels and creates priority-aware pickers.
/// Passes ALL subchannels to picker (does not filter or sort).
/// </summary>
internal sealed class ClusterLoadBalancer : LoadBalancer {
    static readonly BalancerState FailureState = new(ConnectivityState.TransientFailure, new ClusterPicker([]));

    readonly IChannelControlHelper _controller;
    readonly ILogger _logger;
    readonly List<Subchannel> _subchannels = [];
    readonly object _lock = new();
    bool _disposed;

    /// <summary>
    /// Creates a new cluster load balancer.
    /// </summary>
    /// <param name="controller">The channel control helper for creating subchannels.</param>
    /// <param name="logger">Logger instance.</param>
    public ClusterLoadBalancer(IChannelControlHelper controller, ILogger logger);

    /// <summary>
    /// Called when resolver reports new addresses.
    /// Creates/removes subchannels to match the new address list.
    /// </summary>
    public override void UpdateChannelState(ChannelState state);

    /// <summary>
    /// Disposes all subchannels.
    /// </summary>
    protected override void Dispose(bool disposing);

    // Internal: UpdateSubchannels - diff current vs new, add/remove as needed
    // Internal: OnSubchannelStateChanged - reconnect on Idle, update picker on any change
    // Internal: UpdatePicker - create new ClusterPicker with ALL subchannels
}
```

**Key Implementation Details:**

```csharp
void UpdatePicker() {
    // Determine overall connectivity state
    var hasReady = false;
    var hasConnecting = false;
    foreach (var s in _subchannels) {
        if (s.State == ConnectivityState.Ready) hasReady = true;
        else if (s.State == ConnectivityState.Connecting) hasConnecting = true;
    }

    var connectivityState = hasReady
        ? ConnectivityState.Ready
        : hasConnecting
            ? ConnectivityState.Connecting
            : _subchannels.Count > 0
                ? ConnectivityState.TransientFailure
                : ConnectivityState.Idle;

    // Pass ALL subchannels to picker - it decides what to do
    var picker = new ClusterPicker(_subchannels);
    _controller.UpdateState(new BalancerState(connectivityState, picker));
}
```

---

### 13.8 ClusterLoadBalancerFactory

```csharp
namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Factory for creating cluster load balancers.
/// Registered with gRPC's service provider.
/// </summary>
internal sealed class ClusterLoadBalancerFactory : LoadBalancerFactory {
    /// <summary>
    /// The policy name used by this load balancer.
    /// </summary>
    public const string PolicyName = "cluster";

    readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Creates a new load balancer factory.
    /// </summary>
    /// <param name="loggerFactory">Logger factory instance.</param>
    public ClusterLoadBalancerFactory(ILoggerFactory? loggerFactory = null);

    /// <inheritdoc />
    public override string Name => PolicyName;

    /// <inheritdoc />
    public override LoadBalancer Create(LoadBalancerOptions options);
}
```

---

### 13.9 ClusterPicker

```csharp
namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Zero-allocation subchannel picker that implements round-robin within sorted Ready subchannels.
/// Sorts by order index (assigned by Resolver using topology source's IComparer).
/// </summary>
internal sealed class ClusterPicker : SubchannelPicker {
    /// <summary>
    /// Attribute key for storing order index (from topology source comparer).
    /// </summary>
    public const string OrderIndexKey = "cluster-node-order";

    static readonly Status NoReadyNodesStatus =
        new(StatusCode.Unavailable, "No ready nodes available in cluster.");

    readonly Subchannel[] _readySubchannels;
    int _roundRobinIndex;

    /// <summary>
    /// Creates a new picker with the specified subchannels.
    /// Filters to Ready subchannels and sorts by order index.
    /// </summary>
    /// <param name="subchannels">All subchannels from the load balancer.</param>
    public ClusterPicker(IReadOnlyList<Subchannel> subchannels) {
        // Filter to Ready subchannels
        var ready = new List<Subchannel>();
        foreach (var s in subchannels)
            if (s.State == ConnectivityState.Ready)
                ready.Add(s);

        if (ready.Count == 0) {
            _readySubchannels = [];
            return;
        }

        // Sort by order index (assigned by Resolver using topology source comparer)
        ready.Sort((a, b) => GetOrderIndex(a).CompareTo(GetOrderIndex(b)));
        _readySubchannels = [.. ready];
    }

    /// <summary>
    /// Pick a subchannel for the request.
    /// ZERO ALLOCATIONS - called on every gRPC call.
    /// </summary>
    public override PickResult Pick(PickContext context) {
        if (_readySubchannels.Length == 0)
            return PickResult.ForFailure(NoReadyNodesStatus);

        var index = Interlocked.Increment(ref _roundRobinIndex);
        var count = _readySubchannels.Length;
        var selectedIndex = ((index % count) + count) % count;

        return PickResult.ForSubchannel(_readySubchannels[selectedIndex]);
    }

    static int GetOrderIndex(Subchannel subchannel) {
        if (subchannel.Attributes.TryGetValue(OrderIndexKey, out var value) && value is int order)
            return order;
        return int.MaxValue;
    }
}
```

---

### 13.10 RefreshTriggerInterceptor

```csharp
namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Interceptor that triggers topology refresh on configurable RPC exceptions.
/// Wraps all four gRPC call types: Unary, ClientStreaming, ServerStreaming, DuplexStreaming.
/// </summary>
internal sealed class RefreshTriggerInterceptor : Interceptor {
    readonly ShouldRefreshTopology _policy;
    readonly Action _triggerRefresh;
    readonly ILogger _logger;

    /// <summary>
    /// Creates a new refresh trigger interceptor.
    /// </summary>
    /// <param name="policy">The policy that determines when to refresh.</param>
    /// <param name="triggerRefresh">Action to call to trigger refresh.</param>
    /// <param name="logger">Logger instance.</param>
    public RefreshTriggerInterceptor(
        ShouldRefreshTopology policy,
        Action triggerRefresh,
        ILogger logger);

    /// <summary>
    /// Intercept unary calls - wraps response task to catch RpcException.
    /// </summary>
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(...);

    /// <summary>
    /// Intercept client streaming calls - wraps response task.
    /// </summary>
    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(...);

    /// <summary>
    /// Intercept server streaming calls - wraps response stream with RefreshTriggerStreamReader.
    /// </summary>
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(...);

    /// <summary>
    /// Intercept duplex streaming calls - wraps response stream with RefreshTriggerStreamReader.
    /// </summary>
    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(...);

    // Internal: HandleResponse - awaits response, catches RpcException, checks policy
    // Internal: CheckAndTriggerRefresh - if policy returns true, calls _triggerRefresh

    /// <summary>
    /// Stream reader wrapper that checks for refresh triggers on MoveNext.
    /// </summary>
    sealed class RefreshTriggerStreamReader<T> : IAsyncStreamReader<T> {
        public T Current { get; }
        public Task<bool> MoveNext(CancellationToken cancellationToken);
    }
}
```

---

### 13.11 Exception Classes

```csharp
namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Base exception for load balancing errors.
/// </summary>
public abstract class LoadBalancingException : Exception {
    protected LoadBalancingException(string message);
    protected LoadBalancingException(string message, Exception innerException);
}

/// <summary>
/// Configuration is invalid or incomplete.
/// </summary>
public sealed class LoadBalancingConfigurationException : LoadBalancingException {
    public LoadBalancingConfigurationException(string message);
}

/// <summary>
/// Failed to discover cluster topology after all attempts.
/// </summary>
public sealed class ClusterDiscoveryException : LoadBalancingException {
    /// <summary>The number of discovery attempts made.</summary>
    public int Attempts { get; }

    /// <summary>The endpoints that were tried.</summary>
    public ImmutableArray<DnsEndPoint> TriedEndpoints { get; }

    /// <summary>The exceptions that occurred during discovery attempts.</summary>
    public ImmutableArray<Exception> Exceptions { get; }

    public ClusterDiscoveryException(
        int attempts,
        ImmutableArray<DnsEndPoint> triedEndpoints,
        ImmutableArray<Exception> exceptions);
}

/// <summary>
/// No eligible nodes available in the cluster.
/// </summary>
public sealed class NoEligibleNodesException : LoadBalancingException {
    /// <summary>The total number of nodes in the cluster.</summary>
    public int TotalNodes { get; }

    public NoEligibleNodesException(int totalNodes);
}

/// <summary>
/// Topology operation failed.
/// </summary>
public sealed class TopologyException : LoadBalancingException {
    /// <summary>The endpoint where the topology operation failed.</summary>
    public DnsEndPoint Endpoint { get; }

    public TopologyException(DnsEndPoint endpoint, Exception innerException);
}
```

---

### 13.12 Log Messages (Source-Generated)

```csharp
namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Source-generated logging for load balancing operations.
/// Uses [LoggerMessage] attribute for zero-allocation logging.
/// </summary>
internal static partial class Log {
    // Discovery
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Discovering cluster from {Endpoint}")]
    public static partial void DiscoveringCluster(this ILogger logger, DnsEndPoint endpoint);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Discovered {NodeCount} nodes, {EligibleCount} eligible")]
    public static partial void DiscoveredNodes(this ILogger logger, int nodeCount, int eligibleCount);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Topology call to {Endpoint} failed")]
    public static partial void TopologyCallFailed(this ILogger logger, DnsEndPoint endpoint, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Topology stream from {Endpoint} ended without returning any data")]
    public static partial void TopologyStreamEmpty(this ILogger logger, DnsEndPoint endpoint);

    // Topology changes
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Topology changed: {AddedCount} added, {RemovedCount} removed")]
    public static partial void TopologyChanged(this ILogger logger, int addedCount, int removedCount);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No eligible nodes in topology, total nodes: {TotalNodes}")]
    public static partial void NoEligibleNodes(this ILogger logger, int totalNodes);

    // Refresh
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Topology refresh triggered by status code {StatusCode}")]
    public static partial void RefreshTriggered(this ILogger logger, StatusCode statusCode);

    // Seed channel pool
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Creating seed channel to {Endpoint}")]
    public static partial void CreatingSeedChannel(this ILogger logger, DnsEndPoint endpoint);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Disposing seed channel pool with {ChannelCount} channels")]
    public static partial void DisposingSeedChannelPool(this ILogger logger, int channelCount);

    // Polling adapter backoff
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Polling failed (attempt {Attempt}), retrying after {Backoff}")]
    public static partial void PollingFailedRetrying(this ILogger logger, int attempt, TimeSpan backoff, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Polling failed after {Attempts} attempts, giving up on this seed")]
    public static partial void PollingMaxAttemptsExceeded(this ILogger logger, int attempts, Exception exception);
}
```

---

## Summary

This load balancer provides:

- **Simple API**: Implement one interface, no generics needed
- **Clear Separation**: Resolver discovers, LoadBalancer connects, Picker selects
- **High Performance**: Zero allocations on hot path
- **Flexible Extension**: Metadata dictionary for custom node attributes
- **Flexible Selection**: Custom comparer for any selection logic
- **Resilient**: Automatic seed failover, refresh on errors
- **Observable**: Source-generated structured logging

The implementation follows gRPC's native load balancing patterns while providing a much simpler developer experience.
