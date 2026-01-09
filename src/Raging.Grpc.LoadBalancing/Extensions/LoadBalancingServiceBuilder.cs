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

    // Topology source configuration
    Type? _topologySourceType;
    object? _topologySourceInstance;
    Func<IServiceProvider, IStreamingTopologySource>? _streamingSourceFactory;
    Func<IServiceProvider, IPollingTopologySource>? _pollingSourceFactory;
    bool _isStreaming;
    TimeSpan _delay = TimeSpan.FromSeconds(30);

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
    // POLLING TOPOLOGY SOURCE
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Register topology source type for DI resolution.
    /// </summary>
    public LoadBalancingServiceBuilder WithPollingTopologySource<TSource>(TimeSpan? delay = null)
        where TSource : class, IPollingTopologySource {

        _topologySourceType = typeof(TSource);
        _isStreaming = false;
        _delay = delay ?? TimeSpan.FromSeconds(30);
        _topologySourceInstance = null;
        _pollingSourceFactory = null;
        _streamingSourceFactory = null;

        return this;
    }

    /// <summary>
    /// Use topology source instance.
    /// </summary>
    public LoadBalancingServiceBuilder WithPollingTopologySource(
        IPollingTopologySource source,
        TimeSpan? delay = null) {

        _topologySourceInstance = source;
        _isStreaming = false;
        _delay = delay ?? TimeSpan.FromSeconds(30);
        _topologySourceType = null;
        _pollingSourceFactory = null;
        _streamingSourceFactory = null;

        return this;
    }

    /// <summary>
    /// Use factory for topology source creation.
    /// </summary>
    public LoadBalancingServiceBuilder WithPollingTopologySource(
        Func<IServiceProvider, IPollingTopologySource> factory,
        TimeSpan? delay = null) {

        _pollingSourceFactory = factory;
        _isStreaming = false;
        _delay = delay ?? TimeSpan.FromSeconds(30);
        _topologySourceType = null;
        _topologySourceInstance = null;
        _streamingSourceFactory = null;

        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // STREAMING TOPOLOGY SOURCE
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Register streaming topology source type for DI resolution.
    /// </summary>
    public LoadBalancingServiceBuilder WithStreamingTopologySource<TSource>()
        where TSource : class, IStreamingTopologySource {

        _topologySourceType = typeof(TSource);
        _isStreaming = true;
        _topologySourceInstance = null;
        _pollingSourceFactory = null;
        _streamingSourceFactory = null;

        return this;
    }

    /// <summary>
    /// Use streaming topology source instance.
    /// </summary>
    public LoadBalancingServiceBuilder WithStreamingTopologySource(IStreamingTopologySource source) {
        _topologySourceInstance = source;
        _isStreaming = true;
        _topologySourceType = null;
        _pollingSourceFactory = null;
        _streamingSourceFactory = null;

        return this;
    }

    /// <summary>
    /// Use factory for streaming topology source creation.
    /// </summary>
    public LoadBalancingServiceBuilder WithStreamingTopologySource(
        Func<IServiceProvider, IStreamingTopologySource> factory) {

        _streamingSourceFactory = factory;
        _isStreaming = true;
        _topologySourceType = null;
        _topologySourceInstance = null;
        _pollingSourceFactory = null;

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
        if (_topologySourceType is null && _topologySourceInstance is null
            && _pollingSourceFactory is null && _streamingSourceFactory is null) {
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
        var delay = _delay;
        var refreshPolicy = _refreshPolicy ?? RefreshPolicy.Default;
        var configureChannel = _configureChannel;
        var useTls = _useTls;
        var isStreaming = _isStreaming;
        var topologySourceType = _topologySourceType;
        var topologySourceInstance = _topologySourceInstance;
        var pollingSourceFactory = _pollingSourceFactory;
        var streamingSourceFactory = _streamingSourceFactory;

        // Register topology source type if needed
        if (topologySourceType is not null) {
            _services.AddSingleton(topologySourceType);
        }

        // Register the channel factory
        _services.AddSingleton(sp => {
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
            IStreamingTopologySource streamingSource;

            if (isStreaming) {
                IStreamingTopologySource? source = null;

                if (topologySourceInstance is IStreamingTopologySource instance) {
                    source = instance;
                }
                else if (streamingSourceFactory is not null) {
                    source = streamingSourceFactory(sp);
                }
                else if (topologySourceType is not null) {
                    source = (IStreamingTopologySource)sp.GetRequiredService(topologySourceType);
                }

                streamingSource = source ?? throw new InvalidOperationException("Could not resolve streaming topology source.");
            }
            else {
                IPollingTopologySource? pollingSource = null;

                if (topologySourceInstance is IPollingTopologySource instance) {
                    pollingSource = instance;
                }
                else if (pollingSourceFactory is not null) {
                    pollingSource = pollingSourceFactory(sp);
                }
                else if (topologySourceType is not null) {
                    pollingSource = (IPollingTopologySource)sp.GetRequiredService(topologySourceType);
                }

                if (pollingSource is null) {
                    throw new InvalidOperationException("Could not resolve polling topology source.");
                }

                streamingSource = new PollingToStreamingAdapter(
                    pollingSource,
                    delay,
                    resilience,
                    loggerFactory?.CreateLogger<PollingToStreamingAdapter>());
            }

            // Create resolver factory
            var resolverFactory = new ClusterResolverFactory(
                streamingSource,
                [.. seeds],
                resilience,
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
        });

        return _services;
    }
}
