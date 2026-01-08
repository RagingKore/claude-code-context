using System.Net;
using Grpc.Net.Client;
using Grpc.Net.Client.Balancer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Raging.Grpc.LoadBalancing.Internal;
using Raging.Grpc.LoadBalancing.Utilities;

namespace Raging.Grpc.LoadBalancing;

/// <summary>
/// Builder for configuring load balancing with dependency injection.
/// </summary>
public sealed class LoadBalancingServiceBuilder {
    readonly IServiceCollection _services;
    readonly string _address;
    readonly List<DnsEndPoint> _seeds = [];
    ResilienceOptions _resilience = new();

    // Type registration info
    Type? _topologySourceType;
    Type? _nodeType;
    object? _topologySourceInstance;
    Func<IServiceProvider, object>? _topologySourceFactory;
    bool _isStreaming;
    TimeSpan? _delay;

    ShouldRefreshTopology? _refreshPolicy;
    Action<GrpcChannelOptions>? _configureChannel;
    bool _useTls;

    /// <summary>
    /// Creates a new service builder.
    /// </summary>
    internal LoadBalancingServiceBuilder(IServiceCollection services, string address) {
        _services = services;
        _address = address;
    }

    // ═══════════════════════════════════════════════════════════════
    // SEEDS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Add seeds as strings. Format: "host:port"
    /// </summary>
    public LoadBalancingServiceBuilder WithSeeds(params string[] endpoints) {
        foreach (var endpoint in endpoints) {
            _seeds.Add(EndpointParser.Parse(endpoint));
        }

        return this;
    }

    /// <summary>
    /// Add seeds as DnsEndPoints.
    /// </summary>
    public LoadBalancingServiceBuilder WithSeeds(params DnsEndPoint[] endpoints) {
        _seeds.AddRange(endpoints);
        return this;
    }

    /// <summary>
    /// Add seeds from enumerable.
    /// </summary>
    public LoadBalancingServiceBuilder WithSeeds(IEnumerable<DnsEndPoint> endpoints) {
        _seeds.AddRange(endpoints);
        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // RESILIENCE
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Configure resilience options.
    /// </summary>
    public LoadBalancingServiceBuilder WithResilience(Action<ResilienceOptions> configure) {
        configure(_resilience);
        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // POLLING TOPOLOGY SOURCE (3 overloads)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Register topology source type for DI resolution.
    /// </summary>
    public LoadBalancingServiceBuilder WithPollingTopologySource<TNode, TSource>(
        TimeSpan? delay = null)
        where TNode : struct, IClusterNode
        where TSource : class, IPollingTopologySource<TNode> {

        _topologySourceType = typeof(TSource);
        _nodeType = typeof(TNode);
        _isStreaming = false;
        _delay = delay ?? TimeSpan.FromSeconds(30);
        _topologySourceInstance = null;
        _topologySourceFactory = null;

        return this;
    }

    /// <summary>
    /// Use topology source instance.
    /// </summary>
    public LoadBalancingServiceBuilder WithPollingTopologySource<TNode>(
        IPollingTopologySource<TNode> source,
        TimeSpan? delay = null)
        where TNode : struct, IClusterNode {

        _topologySourceInstance = source;
        _nodeType = typeof(TNode);
        _isStreaming = false;
        _delay = delay ?? TimeSpan.FromSeconds(30);
        _topologySourceType = null;
        _topologySourceFactory = null;

        return this;
    }

    /// <summary>
    /// Use factory for topology source creation.
    /// </summary>
    public LoadBalancingServiceBuilder WithPollingTopologySource<TNode>(
        Func<IServiceProvider, IPollingTopologySource<TNode>> factory,
        TimeSpan? delay = null)
        where TNode : struct, IClusterNode {

        _topologySourceFactory = sp => factory(sp);
        _nodeType = typeof(TNode);
        _isStreaming = false;
        _delay = delay ?? TimeSpan.FromSeconds(30);
        _topologySourceType = null;
        _topologySourceInstance = null;

        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // STREAMING TOPOLOGY SOURCE (3 overloads)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Register streaming topology source type for DI resolution.
    /// </summary>
    public LoadBalancingServiceBuilder WithStreamingTopologySource<TNode, TSource>()
        where TNode : struct, IClusterNode
        where TSource : class, IStreamingTopologySource<TNode> {

        _topologySourceType = typeof(TSource);
        _nodeType = typeof(TNode);
        _isStreaming = true;
        _delay = null;
        _topologySourceInstance = null;
        _topologySourceFactory = null;

        return this;
    }

    /// <summary>
    /// Use streaming topology source instance.
    /// </summary>
    public LoadBalancingServiceBuilder WithStreamingTopologySource<TNode>(
        IStreamingTopologySource<TNode> source)
        where TNode : struct, IClusterNode {

        _topologySourceInstance = source;
        _nodeType = typeof(TNode);
        _isStreaming = true;
        _delay = null;
        _topologySourceType = null;
        _topologySourceFactory = null;

        return this;
    }

    /// <summary>
    /// Use factory for streaming topology source creation.
    /// </summary>
    public LoadBalancingServiceBuilder WithStreamingTopologySource<TNode>(
        Func<IServiceProvider, IStreamingTopologySource<TNode>> factory)
        where TNode : struct, IClusterNode {

        _topologySourceFactory = sp => factory(sp);
        _nodeType = typeof(TNode);
        _isStreaming = true;
        _delay = null;
        _topologySourceType = null;
        _topologySourceInstance = null;

        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // CONFIGURATION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Custom refresh policy.
    /// </summary>
    public LoadBalancingServiceBuilder WithRefreshPolicy(ShouldRefreshTopology policy) {
        _refreshPolicy = policy;
        return this;
    }

    /// <summary>
    /// Configure the underlying GrpcChannel.
    /// </summary>
    public LoadBalancingServiceBuilder ConfigureChannel(Action<GrpcChannelOptions> configure) {
        _configureChannel = configure;
        return this;
    }

    /// <summary>
    /// Use TLS for connections.
    /// </summary>
    public LoadBalancingServiceBuilder UseTls(bool useTls = true) {
        _useTls = useTls;
        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // BUILD
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Register the configured channel in the service collection.
    /// </summary>
    public IServiceCollection Build() {
        // Validate configuration
        if (_nodeType is null) {
            throw new LoadBalancingConfigurationException(
                "Topology source must be configured. Call WithPollingTopologySource or WithStreamingTopologySource.");
        }

        // Parse primary endpoint
        var primaryEndpoint = EndpointParser.Parse(_address);

        // Build seed list with primary endpoint first
        var allSeeds = new List<DnsEndPoint> { primaryEndpoint };

        foreach (var seed in _seeds) {
            if (seed.Host != primaryEndpoint.Host || seed.Port != primaryEndpoint.Port) {
                allSeeds.Add(seed);
            }
        }

        // Capture configuration for the factory
        var seeds = allSeeds.ToArray();
        var resilience = _resilience;
        var delay = _delay ?? TimeSpan.FromSeconds(30);
        var refreshPolicy = _refreshPolicy ?? RefreshPolicy.Default;
        var configureChannel = _configureChannel;
        var useTls = _useTls;
        var isStreaming = _isStreaming;
        var nodeType = _nodeType;
        var topologySourceType = _topologySourceType;
        var topologySourceInstance = _topologySourceInstance;
        var topologySourceFactory = _topologySourceFactory;

        // Register topology source type if needed
        if (topologySourceType is not null) {
            _services.AddSingleton(topologySourceType);
        }

        // Register the channel factory
        _services.AddSingleton(sp => {
            var method = typeof(LoadBalancingServiceBuilder)
                .GetMethod(nameof(CreateChannelGeneric), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(nodeType);

            return (GrpcChannel)method.Invoke(null, [
                sp,
                seeds,
                resilience,
                delay,
                refreshPolicy,
                configureChannel,
                useTls,
                isStreaming,
                topologySourceType,
                topologySourceInstance,
                topologySourceFactory
            ])!;
        });

        return _services;
    }

    static GrpcChannel CreateChannelGeneric<TNode>(
        IServiceProvider sp,
        DnsEndPoint[] seeds,
        ResilienceOptions resilience,
        TimeSpan delay,
        ShouldRefreshTopology refreshPolicy,
        Action<GrpcChannelOptions>? configureChannel,
        bool useTls,
        bool isStreaming,
        Type? topologySourceType,
        object? topologySourceInstance,
        Func<IServiceProvider, object>? topologySourceFactory)
        where TNode : struct, IClusterNode {

        var loggerFactory = sp.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<SeedChannelPool>();

        // Create channel options for seed channels
        var seedChannelOptions = new GrpcChannelOptions();
        configureChannel?.Invoke(seedChannelOptions);

        // Create seed channel pool
        var seedChannelPool = new SeedChannelPool(
            seedChannelOptions,
            useTls,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        // Get or create topology source
        IStreamingTopologySource<TNode> streamingSource;

        if (isStreaming) {
            IStreamingTopologySource<TNode>? source = null;

            if (topologySourceInstance is not null) {
                source = (IStreamingTopologySource<TNode>)topologySourceInstance;
            }
            else if (topologySourceFactory is not null) {
                source = (IStreamingTopologySource<TNode>)topologySourceFactory(sp);
            }
            else if (topologySourceType is not null) {
                source = (IStreamingTopologySource<TNode>)sp.GetRequiredService(topologySourceType);
            }

            streamingSource = source ?? throw new InvalidOperationException("Could not resolve streaming topology source.");
        }
        else {
            IPollingTopologySource<TNode>? pollingSource = null;

            if (topologySourceInstance is not null) {
                pollingSource = (IPollingTopologySource<TNode>)topologySourceInstance;
            }
            else if (topologySourceFactory is not null) {
                pollingSource = (IPollingTopologySource<TNode>)topologySourceFactory(sp);
            }
            else if (topologySourceType is not null) {
                pollingSource = (IPollingTopologySource<TNode>)sp.GetRequiredService(topologySourceType);
            }

            if (pollingSource is null) {
                throw new InvalidOperationException("Could not resolve polling topology source.");
            }

            streamingSource = new PollingToStreamingAdapter<TNode>(pollingSource, delay);
        }

        // Create resolver factory
        var resolverFactory = new ClusterResolverFactory<TNode>(
            streamingSource,
            seeds,
            resilience,
            delay,
            seedChannelPool,
            loggerFactory);

        // Create load balancer factory
        var loadBalancerFactory = new ClusterLoadBalancerFactory(loggerFactory);

        // Build inner service provider for gRPC
        var grpcServices = new ServiceCollection();
        grpcServices.AddSingleton<ResolverFactory>(resolverFactory);
        grpcServices.AddSingleton<LoadBalancerFactory>(loadBalancerFactory);
        var grpcServiceProvider = grpcServices.BuildServiceProvider();

        // Build channel options
        var channelOptions = new GrpcChannelOptions {
            ServiceProvider = grpcServiceProvider
        };

        configureChannel?.Invoke(channelOptions);

        // Add refresh trigger interceptor
        var interceptor = new RefreshTriggerInterceptor(
            refreshPolicy,
            () => { }, // Refresh is handled by load balancer infrastructure
            loggerFactory?.CreateLogger<RefreshTriggerInterceptor>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RefreshTriggerInterceptor>.Instance);

        var existingInterceptors = channelOptions.Interceptors?.ToList() ?? [];
        existingInterceptors.Insert(0, interceptor);
        channelOptions.Interceptors = existingInterceptors;

        // Create channel with custom scheme
        var channelId = Guid.NewGuid();
        return GrpcChannel.ForAddress($"cluster:///{channelId}", channelOptions);
    }
}
