using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;

namespace Raging.Grpc.LoadBalancing.Internal;

/// <summary>
/// Interceptor that triggers topology refresh on configurable RPC exceptions.
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
        ILogger logger) {

        _policy = policy;
        _triggerRefresh = triggerRefresh;
        _logger = logger;
    }

    /// <inheritdoc />
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation) {

        var call = continuation(request, context);

        return new AsyncUnaryCall<TResponse>(
            HandleResponse(call.ResponseAsync),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    /// <inheritdoc />
    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation) {

        var call = continuation(context);

        return new AsyncClientStreamingCall<TRequest, TResponse>(
            call.RequestStream,
            HandleResponse(call.ResponseAsync),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    /// <inheritdoc />
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation) {

        var call = continuation(request, context);

        return new AsyncServerStreamingCall<TResponse>(
            new RefreshTriggerStreamReader<TResponse>(call.ResponseStream, _policy, _triggerRefresh, _logger),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    /// <inheritdoc />
    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation) {

        var call = continuation(context);

        return new AsyncDuplexStreamingCall<TRequest, TResponse>(
            call.RequestStream,
            new RefreshTriggerStreamReader<TResponse>(call.ResponseStream, _policy, _triggerRefresh, _logger),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    async Task<TResponse> HandleResponse<TResponse>(Task<TResponse> responseTask) {
        try {
            return await responseTask.ConfigureAwait(false);
        }
        catch (RpcException ex) {
            CheckAndTriggerRefresh(ex);
            throw;
        }
    }

    void CheckAndTriggerRefresh(RpcException ex) {
        if (_policy(ex)) {
            _logger.RefreshTriggered(ex.StatusCode);
            _triggerRefresh();
        }
    }

    /// <summary>
    /// Stream reader wrapper that checks for refresh triggers.
    /// </summary>
    sealed class RefreshTriggerStreamReader<T> : IAsyncStreamReader<T> {
        readonly IAsyncStreamReader<T> _inner;
        readonly ShouldRefreshTopology _policy;
        readonly Action _triggerRefresh;
        readonly ILogger _logger;

        public RefreshTriggerStreamReader(
            IAsyncStreamReader<T> inner,
            ShouldRefreshTopology policy,
            Action triggerRefresh,
            ILogger logger) {

            _inner = inner;
            _policy = policy;
            _triggerRefresh = triggerRefresh;
            _logger = logger;
        }

        public T Current => _inner.Current;

        public async Task<bool> MoveNext(CancellationToken cancellationToken) {
            try {
                return await _inner.MoveNext(cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException ex) {
                if (_policy(ex)) {
                    _logger.RefreshTriggered(ex.StatusCode);
                    _triggerRefresh();
                }

                throw;
            }
        }
    }
}
