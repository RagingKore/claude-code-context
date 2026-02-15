using System.Collections.Concurrent;
using System.Text.Json;
using EventStore.Client;

namespace ActorConsensus.AkkaDiscoveryKurrentDb;

/// <summary>
/// Options for the KurrentDB-backed membership service.
/// </summary>
public sealed class KurrentDbMembershipOptions
{
    /// <summary>KurrentDB connection string.</summary>
    public string ConnectionString { get; set; } = "esdb://localhost:2113?tls=false";

    /// <summary>Logical service name — used as the stream suffix.</summary>
    public string ServiceName { get; set; } = "default";

    /// <summary>How often this node writes a heartbeat event.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long since the last heartbeat before a node is considered dead.</summary>
    public TimeSpan HeartbeatTtl { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>This node's advertised hostname.</summary>
    public string PublicHostname { get; set; } = "";

    /// <summary>This node's advertised port.</summary>
    public int PublicPort { get; set; }

    /// <summary>Stream name prefix. Full name = {prefix}{service-name}.</summary>
    public string StreamPrefix { get; set; } = "discovery-";
}

/// <summary>
/// A single member in the membership view.
/// </summary>
public sealed record MemberEntry(string NodeId, string Host, int Port, DateTimeOffset LastSeen);

/// <summary>
/// Simple membership service backed by KurrentDB.
///
/// Writer: a timer that appends <see cref="NodeHeartbeat"/> events to a well-known stream.
/// Reader: a catch-up subscription that builds a local view of who's alive.
/// Timeout: nodes without a heartbeat within <see cref="KurrentDbMembershipOptions.HeartbeatTtl"/> are dead.
/// </summary>
public sealed class KurrentDbMembershipService : IAsyncDisposable
{
    private readonly EventStoreClient _client;
    private readonly KurrentDbMembershipOptions _options;
    private readonly string _streamName;
    private readonly string _nodeId;
    private readonly ConcurrentDictionary<string, MemberEntry> _members = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _heartbeatTimer;
    private readonly Task _subscriptionTask;

    public KurrentDbMembershipService(KurrentDbMembershipOptions options)
    {
        _options = options;
        _client = new EventStoreClient(EventStoreClientSettings.Create(options.ConnectionString));

        var hostname = string.IsNullOrEmpty(options.PublicHostname)
            ? System.Net.Dns.GetHostName()
            : options.PublicHostname;

        _streamName = $"{options.StreamPrefix}{options.ServiceName}";
        _nodeId = $"{hostname}:{options.PublicPort}";

        // Writer: periodic heartbeat
        _heartbeatTimer = new Timer(
            _ => _ = WriteHeartbeatAsync(),
            null,
            TimeSpan.Zero,
            options.HeartbeatInterval);

        // Reader: catch-up subscription
        _subscriptionTask = RunSubscriptionAsync(_cts.Token);
    }

    /// <summary>This node's identifier (host:port).</summary>
    public string NodeId => _nodeId;

    /// <summary>
    /// Returns all nodes whose last heartbeat is within the TTL window.
    /// </summary>
    public IReadOnlyList<MemberEntry> GetAliveMembers()
    {
        var cutoff = DateTimeOffset.UtcNow - _options.HeartbeatTtl;
        return _members.Values.Where(m => m.LastSeen >= cutoff).ToList();
    }

    private async Task WriteHeartbeatAsync()
    {
        try
        {
            var heartbeat = new NodeHeartbeat(_nodeId, _options.PublicHostname, _options.PublicPort, DateTimeOffset.UtcNow);
            var eventData = new EventData(
                Uuid.NewUuid(),
                nameof(NodeHeartbeat),
                JsonSerializer.SerializeToUtf8Bytes(heartbeat));

            await _client.AppendToStreamAsync(_streamName, StreamState.Any, [eventData], cancellationToken: _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Heartbeat failures are transient — next tick will retry
        }
    }

    private async Task WriteLeaveAsync()
    {
        var left = new NodeLeft(_nodeId, DateTimeOffset.UtcNow);
        var eventData = new EventData(
            Uuid.NewUuid(),
            nameof(NodeLeft),
            JsonSerializer.SerializeToUtf8Bytes(left));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _client.AppendToStreamAsync(_streamName, StreamState.Any, [eventData], cancellationToken: timeout.Token);
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
                                _members[hb.NodeId] = new MemberEntry(hb.NodeId, hb.Host, hb.Port, hb.Timestamp);
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

    public async ValueTask DisposeAsync()
    {
        await _heartbeatTimer.DisposeAsync();
        await _cts.CancelAsync();

        try { await _subscriptionTask; }
        catch (OperationCanceledException) { }

        try { await WriteLeaveAsync(); }
        catch { /* best-effort on shutdown */ }

        _client.Dispose();
        _cts.Dispose();
    }
}
