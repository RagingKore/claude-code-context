using System.Collections.Concurrent;
using System.Text.Json;
using EventStore.Client;

namespace ActorConsensus.KurrentDbMembership;

/// <summary>
/// A single membership node backed by KurrentDB — no actor framework required.
///
/// Writer: timer appends <see cref="NodeHeartbeat"/> events to a shared stream.
/// Reader: catch-up subscription builds a local view of all live nodes.
/// Leader: highest alive NodeId wins (Bully-style) via <see cref="LeaderClaimed"/> events.
/// Work:   leader simulates processing by incrementing a counter each heartbeat tick.
/// </summary>
public sealed class MembershipNode : IAsyncDisposable
{
    private readonly EventStoreClient _client;
    private readonly MembershipOptions _options;
    private readonly string _streamName;
    private readonly string _nodeId;
    private readonly ConcurrentDictionary<string, MemberView> _members = new();
    private readonly CancellationTokenSource _cts = new();
    private Timer? _heartbeatTimer;
    private Task? _subscriptionTask;
    private Task? _electionTask;

    private long _currentTerm;
    private string? _currentLeader;
    private bool _alive;
    private int _workItemsProcessed;

    public MembershipNode(MembershipOptions options)
    {
        _options = options;
        _client = new EventStoreClient(EventStoreClientSettings.Create(options.ConnectionString));
        _streamName = $"{options.StreamPrefix}{options.ServiceName}";
        _nodeId = $"node-{options.NodeId}";
    }

    public int NodeId => _options.NodeId;
    public string NodeIdentifier => _nodeId;
    public bool IsAlive => _alive;
    public bool IsLeader => _currentLeader == _nodeId && _alive;
    public string? CurrentLeader => _currentLeader;
    public long CurrentTerm => _currentTerm;
    public int WorkItemsProcessed => _workItemsProcessed;

    /// <summary>
    /// Returns all nodes whose last heartbeat is within the TTL window.
    /// </summary>
    public IReadOnlyList<MemberView> GetAliveMembers()
    {
        var cutoff = DateTimeOffset.UtcNow - _options.HeartbeatTtl;
        return _members.Values.Where(m => m.LastSeen >= cutoff).ToList();
    }

    /// <summary>Start participating in the cluster.</summary>
    public Task StartAsync()
    {
        _alive = true;

        // Writer: periodic heartbeat
        _heartbeatTimer = new Timer(
            _ => _ = WriteHeartbeatAsync(),
            null,
            TimeSpan.Zero,
            _options.HeartbeatInterval);

        // Reader: catch-up subscription
        _subscriptionTask = RunSubscriptionAsync(_cts.Token);

        // Election monitor
        _electionTask = RunElectionMonitorAsync(_cts.Token);

        return Task.CompletedTask;
    }

    /// <summary>Simulate node crash — stop heartbeating and participating.</summary>
    public async Task StopAsync()
    {
        _alive = false;

        if (_heartbeatTimer is not null)
            await _heartbeatTimer.DisposeAsync();
        _heartbeatTimer = null;

        try
        {
            var left = new NodeLeft(_nodeId, DateTimeOffset.UtcNow);
            var eventData = new EventData(
                Uuid.NewUuid(),
                nameof(NodeLeft),
                JsonSerializer.SerializeToUtf8Bytes(left));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _client.AppendToStreamAsync(_streamName, StreamState.Any, [eventData], cancellationToken: timeout.Token);
        }
        catch
        {
            // best-effort on stop
        }
    }

    private async Task WriteHeartbeatAsync()
    {
        if (!_alive) return;

        try
        {
            // Simulate work if leader
            if (IsLeader)
                Interlocked.Increment(ref _workItemsProcessed);

            var heartbeat = new NodeHeartbeat(_nodeId, _options.Host, _options.Port, IsLeader, DateTimeOffset.UtcNow);
            var eventData = new EventData(
                Uuid.NewUuid(),
                nameof(NodeHeartbeat),
                JsonSerializer.SerializeToUtf8Bytes(heartbeat));

            await _client.AppendToStreamAsync(_streamName, StreamState.Any, [eventData], cancellationToken: _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Heartbeat failures are transient — next tick retries
        }
    }

    private async Task RunSubscriptionAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var subscription = _client.SubscribeToStream(_streamName, FromStream.Start, cancellationToken: ct);

                await foreach (var message in subscription.Messages.WithCancellation(ct))
                {
                    if (message is not StreamMessage.Event(var resolved))
                        continue;

                    switch (resolved.Event.EventType)
                    {
                        case nameof(NodeHeartbeat):
                            var hb = JsonSerializer.Deserialize<NodeHeartbeat>(resolved.Event.Data.Span);
                            if (hb is not null)
                                _members[hb.NodeId] = new MemberView(hb.NodeId, hb.Host, hb.Port, hb.IsLeader, hb.Timestamp);
                            break;

                        case nameof(LeaderClaimed):
                            var claim = JsonSerializer.Deserialize<LeaderClaimed>(resolved.Event.Data.Span);
                            if (claim is not null && claim.Term >= _currentTerm)
                            {
                                _currentTerm = claim.Term;
                                _currentLeader = claim.NodeId;
                            }
                            break;

                        case nameof(NodeLeft):
                            var left = JsonSerializer.Deserialize<NodeLeft>(resolved.Event.Data.Span);
                            if (left is not null)
                                _members.TryRemove(left.NodeId, out _);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // Subscription dropped — wait and retry
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Monitors membership and claims leadership when this node is the
    /// highest-ID alive node and no current leader exists (or leader is dead).
    /// Bully algorithm: highest ID wins, no election rounds needed.
    /// </summary>
    private async Task RunElectionMonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.ElectionDelay, ct);

                if (!_alive) continue;

                var alive = GetAliveMembers();
                var leaderAlive = _currentLeader is not null && alive.Any(m => m.NodeId == _currentLeader);

                if (!leaderAlive)
                {
                    // Is this node the highest-ID alive node?
                    var highestAlive = alive
                        .OrderByDescending(m => m.NodeId, StringComparer.Ordinal)
                        .FirstOrDefault();

                    if (highestAlive?.NodeId == _nodeId)
                    {
                        var newTerm = _currentTerm + 1;
                        var claim = new LeaderClaimed(_nodeId, newTerm, DateTimeOffset.UtcNow);
                        var eventData = new EventData(
                            Uuid.NewUuid(),
                            nameof(LeaderClaimed),
                            JsonSerializer.SerializeToUtf8Bytes(claim));

                        await _client.AppendToStreamAsync(_streamName, StreamState.Any, [eventData], cancellationToken: ct);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // Election monitor errors are transient
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_heartbeatTimer is not null)
            await _heartbeatTimer.DisposeAsync();

        await _cts.CancelAsync();

        if (_subscriptionTask is not null)
        {
            try { await _subscriptionTask; }
            catch (OperationCanceledException) { }
        }

        if (_electionTask is not null)
        {
            try { await _electionTask; }
            catch (OperationCanceledException) { }
        }

        _client.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// Local view of a member as seen through the subscription.
/// </summary>
public sealed record MemberView(string NodeId, string Host, int Port, bool IsLeader, DateTimeOffset LastSeen);
