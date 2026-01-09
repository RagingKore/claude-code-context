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

    IStreamingTopologySource? _streamingSource;
    IPollingTopologySource? _pollingSource;
    TimeSpan _delay = TimeSpan.FromSeconds(30);

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
    public LoadBalancingBuilder WithPollingTopologySource(
        IPollingTopologySource source,
        TimeSpan? delay = null) {

        _pollingSource = source;
        _streamingSource = null;
        _delay = delay ?? TimeSpan.FromSeconds(30);

        return this;
    }

    /// <summary>
    /// Use a streaming topology source.
    /// </summary>
    public LoadBalancingBuilder WithStreamingTopologySource(IStreamingTopologySource source) {
        _streamingSource = source;
        _pollingSource = null;

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
        if (_pollingSource is null && _streamingSource is null) {
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
        IStreamingTopologySource streamingSource = _streamingSource
            ?? new PollingToStreamingAdapter(
                _pollingSource!,
                _delay,
                _resilience,
                loggerFactory?.CreateLogger<PollingToStreamingAdapter>());

        // Create resolver factory
        var resolverFactory = new ClusterResolverFactory(
            streamingSource,
            [.. allSeeds],
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
