using System.Net;
using Grpc.Net.Client;
using Grpc.Net.Client.Balancer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Raging.Grpc.LoadBalancing.Internal;
using Raging.Grpc.LoadBalancing.Utilities;

namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Builder for configuring load balancing without dependency injection.
/// </summary>
public sealed class LoadBalancingBuilder {
    readonly List<DnsEndPoint> _seeds = [];
    ResilienceOptions _resilience = new();

    object? _topologySource;
    Type? _nodeType;
    bool _isStreaming;
    TimeSpan? _delay;

    ShouldRefreshTopology? _refreshPolicy;
    ILoggerFactory? _loggerFactory;
    Action<GrpcChannelOptions>? _configureChannel;
    bool _useTls;

    // ═══════════════════════════════════════════════════════════════
    // SEEDS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Add seeds as strings. Format: "host:port"
    /// </summary>
    public LoadBalancingBuilder WithSeeds(params string[] endpoints) {
        foreach (var endpoint in endpoints) {
            _seeds.Add(EndpointParser.Parse(endpoint));
        }

        return this;
    }

    /// <summary>
    /// Add seeds as DnsEndPoints.
    /// </summary>
    public LoadBalancingBuilder WithSeeds(params DnsEndPoint[] endpoints) {
        _seeds.AddRange(endpoints);
        return this;
    }

    /// <summary>
    /// Add seeds from enumerable.
    /// </summary>
    public LoadBalancingBuilder WithSeeds(IEnumerable<DnsEndPoint> endpoints) {
        _seeds.AddRange(endpoints);
        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // RESILIENCE
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Configure resilience options.
    /// </summary>
    public LoadBalancingBuilder WithResilience(Action<ResilienceOptions> configure) {
        configure(_resilience);
        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // TOPOLOGY SOURCE
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Use a polling topology source.
    /// </summary>
    /// <param name="source">The topology source instance.</param>
    /// <param name="delay">Delay between polls. Default: 30 seconds.</param>
    public LoadBalancingBuilder WithPollingTopologySource<TNode>(
        IPollingTopologySource<TNode> source,
        TimeSpan? delay = null)
        where TNode : struct, IClusterNode {

        _topologySource = source;
        _nodeType = typeof(TNode);
        _isStreaming = false;
        _delay = delay ?? TimeSpan.FromSeconds(30);

        return this;
    }

    /// <summary>
    /// Use a streaming topology source.
    /// </summary>
    public LoadBalancingBuilder WithStreamingTopologySource<TNode>(
        IStreamingTopologySource<TNode> source)
        where TNode : struct, IClusterNode {

        _topologySource = source;
        _nodeType = typeof(TNode);
        _isStreaming = true;
        _delay = null;

        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // REFRESH POLICY
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Custom policy for triggering topology refresh on errors.
    /// </summary>
    public LoadBalancingBuilder WithRefreshPolicy(ShouldRefreshTopology policy) {
        _refreshPolicy = policy;
        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // LOGGING
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Configure logging.
    /// </summary>
    public LoadBalancingBuilder WithLoggerFactory(ILoggerFactory loggerFactory) {
        _loggerFactory = loggerFactory;
        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // CHANNEL OPTIONS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Configure the underlying GrpcChannel.
    /// </summary>
    public LoadBalancingBuilder ConfigureChannel(Action<GrpcChannelOptions> configure) {
        _configureChannel = configure;
        return this;
    }

    /// <summary>
    /// Use TLS for connections.
    /// </summary>
    public LoadBalancingBuilder UseTls(bool useTls = true) {
        _useTls = useTls;
        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // BUILD (internal)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Build the channel for the specified primary endpoint.
    /// </summary>
    internal GrpcChannel Build(DnsEndPoint primaryEndpoint) {
        // Validate configuration
        if (_topologySource is null || _nodeType is null) {
            throw new LoadBalancingConfigurationException(
                "Topology source must be configured. Call WithPollingTopologySource or WithStreamingTopologySource.");
        }

        // Build seed list with primary endpoint first
        var allSeeds = new List<DnsEndPoint> { primaryEndpoint };

        foreach (var seed in _seeds) {
            if (seed.Host != primaryEndpoint.Host || seed.Port != primaryEndpoint.Port) {
                allSeeds.Add(seed);
            }
        }

        if (allSeeds.Count == 0) {
            throw new LoadBalancingConfigurationException(
                "At least one seed endpoint must be configured.");
        }

        // Build using reflection to call the generic method
        var method = typeof(LoadBalancingBuilder)
            .GetMethod(nameof(BuildGeneric), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .MakeGenericMethod(_nodeType);

        return (GrpcChannel)method.Invoke(this, [allSeeds])!;
    }

    GrpcChannel BuildGeneric<TNode>(List<DnsEndPoint> seeds) where TNode : struct, IClusterNode {
        var loggerFactory = _loggerFactory;
        var logger = loggerFactory?.CreateLogger<SeedChannelPool>();

        // Create channel options for seed channels
        var seedChannelOptions = new GrpcChannelOptions();
        _configureChannel?.Invoke(seedChannelOptions);

        // Create seed channel pool
        var seedChannelPool = new SeedChannelPool(
            seedChannelOptions,
            _useTls,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        // Create streaming topology source (wrap polling if needed)
        IStreamingTopologySource<TNode> streamingSource;

        if (_isStreaming) {
            streamingSource = (IStreamingTopologySource<TNode>)_topologySource!;
        }
        else {
            var pollingSource = (IPollingTopologySource<TNode>)_topologySource!;
            streamingSource = new PollingToStreamingAdapter<TNode>(pollingSource, _delay ?? TimeSpan.FromSeconds(30));
        }

        // Create resolver factory
        var resolverFactory = new ClusterResolverFactory<TNode>(
            streamingSource,
            seeds,
            _resilience,
            seedChannelPool,
            loggerFactory);

        // Create load balancer factory
        var loadBalancerFactory = new ClusterLoadBalancerFactory(loggerFactory);

        // Build service provider
        var services = new ServiceCollection();
        services.AddSingleton<ResolverFactory>(resolverFactory);
        services.AddSingleton<LoadBalancerFactory>(loadBalancerFactory);
        var serviceProvider = services.BuildServiceProvider();

        // Build channel options
        var channelOptions = new GrpcChannelOptions {
            ServiceProvider = serviceProvider
        };

        _configureChannel?.Invoke(channelOptions);

        // Get refresh policy
        var refreshPolicy = _refreshPolicy ?? RefreshPolicy.Default;

        // We need a reference to the resolver to trigger refresh
        // This is a bit tricky - we'll use a wrapper that gets set after channel creation
        Action? triggerRefresh = null;

        // Add refresh trigger interceptor
        var interceptor = new RefreshTriggerInterceptor(
            refreshPolicy,
            () => triggerRefresh?.Invoke(),
            loggerFactory?.CreateLogger<RefreshTriggerInterceptor>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RefreshTriggerInterceptor>.Instance);

        var existingInterceptors = channelOptions.Interceptors?.ToList() ?? [];
        existingInterceptors.Insert(0, interceptor);
        channelOptions.Interceptors = existingInterceptors;

        // Create channel with custom scheme
        var channelId = Guid.NewGuid();
        var channel = GrpcChannel.ForAddress($"cluster:///{channelId}", channelOptions);

        // Unfortunately we can't easily get the resolver reference from the channel
        // The refresh will be handled by the load balancer infrastructure detecting failures

        return channel;
    }
}
