using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using EventStore.Client;

namespace ActorConsensus.AkkaDiscoveryKurrentDb;

/// <summary>
/// Internal actor that maintains the KurrentDB-backed membership view.
///
/// Responsibilities:
/// 1. Periodically writes <see cref="NodeHeartbeat"/> events for the local node
/// 2. Subscribes to the discovery stream and processes heartbeat/leave events
/// 3. Maintains an in-memory map of alive nodes (filtered by TTL on query)
/// 4. Responds to <see cref="GetMembers"/> with the current alive set
/// </summary>
internal sealed class MembershipActor : ReceiveActor, IWithTimers
{
    // --- Query messages ---

    /// <summary>Request the current set of alive members.</summary>
    public sealed record GetMembers;

    /// <summary>Response containing alive members filtered by TTL.</summary>
    public sealed record MembersResult(IReadOnlyList<MemberEntry> Entries);

    /// <summary>A single discovered member.</summary>
    public sealed record MemberEntry(string NodeId, string Host, int Port, DateTimeOffset LastSeen);

    // --- Internal messages ---

    private sealed record WriteHeartbeat;
    private sealed record WriteNodeLeft;
    private sealed record EventReceived(string EventType, byte[] Data);
    private sealed record SubscriptionDropped(Exception? Exception);
    private sealed record HeartbeatWritten;
    private sealed record HeartbeatWriteFailed(Exception Exception);

    // --- State ---

    private readonly KurrentDbDiscoverySettings _settings;
    private readonly EventStoreClient _client;
    private readonly ILoggingAdapter _log;
    private readonly Dictionary<string, MemberEntry> _members = new();
    private CancellationTokenSource? _subscriptionCts;

    public ITimerScheduler Timers { get; set; } = null!;

    public static Props CreateProps(KurrentDbDiscoverySettings settings) =>
        Props.Create(() => new MembershipActor(settings));

    public MembershipActor(KurrentDbDiscoverySettings settings)
    {
        _settings = settings;
        _log = Context.GetLogger();

        var clientSettings = EventStoreClientSettings.Create(settings.ConnectionString);
        _client = new EventStoreClient(clientSettings);

        Receive<WriteHeartbeat>(_ => OnWriteHeartbeat());
        Receive<WriteNodeLeft>(_ => OnWriteNodeLeft());
        Receive<EventReceived>(e => OnEventReceived(e));
        Receive<GetMembers>(_ => OnGetMembers());
        Receive<HeartbeatWritten>(_ => { }); // ack, nothing to do
        Receive<HeartbeatWriteFailed>(e => _log.Warning("Heartbeat write failed: {0}", e.Exception.Message));
        Receive<SubscriptionDropped>(e => OnSubscriptionDropped(e));
        Receive<ResubscribeCommand>(_ => OnResubscribe());
    }

    protected override void PreStart()
    {
        base.PreStart();

        // Start periodic heartbeat writes
        Timers.StartPeriodicTimer(
            "heartbeat",
            new WriteHeartbeat(),
            TimeSpan.Zero, // immediate first heartbeat
            _settings.HeartbeatInterval);

        // Start catch-up subscription to the discovery stream
        StartSubscription();
    }

    protected override void PostStop()
    {
        _subscriptionCts?.Cancel();
        _subscriptionCts?.Dispose();

        // Best-effort: write a leave event synchronously
        try
        {
            WriteLeaveEvent().GetAwaiter().GetResult();
        }
        catch
        {
            // Shutdown path — don't throw
        }

        _client.Dispose();
        base.PostStop();
    }

    // --- Handlers ---

    private void OnWriteHeartbeat()
    {
        var heartbeat = new NodeHeartbeat(
            _settings.NodeId,
            _settings.PublicHostname,
            _settings.PublicPort,
            DateTimeOffset.UtcNow);

        var eventData = new EventData(
            Uuid.NewUuid(),
            nameof(NodeHeartbeat),
            JsonSerializer.SerializeToUtf8Bytes(heartbeat));

        _client.AppendToStreamAsync(_settings.StreamName, StreamState.Any, [eventData])
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    return (object)new HeartbeatWriteFailed(t.Exception!.InnerException ?? t.Exception);
                return new HeartbeatWritten();
            })
            .PipeTo(Self);
    }

    private void OnWriteNodeLeft()
    {
        _ = WriteLeaveEvent();
    }

    private async Task WriteLeaveEvent()
    {
        var left = new NodeLeft(_settings.NodeId, DateTimeOffset.UtcNow);
        var eventData = new EventData(
            Uuid.NewUuid(),
            nameof(NodeLeft),
            JsonSerializer.SerializeToUtf8Bytes(left));

        await _client.AppendToStreamAsync(_settings.StreamName, StreamState.Any, [eventData]);
    }

    private void OnEventReceived(EventReceived evt)
    {
        try
        {
            switch (evt.EventType)
            {
                case nameof(NodeHeartbeat):
                    var heartbeat = JsonSerializer.Deserialize<NodeHeartbeat>(evt.Data);
                    if (heartbeat is not null)
                    {
                        _members[heartbeat.NodeId] = new MemberEntry(
                            heartbeat.NodeId,
                            heartbeat.Host,
                            heartbeat.Port,
                            heartbeat.Timestamp);
                    }
                    break;

                case nameof(NodeLeft):
                    var left = JsonSerializer.Deserialize<NodeLeft>(evt.Data);
                    if (left is not null)
                        _members.Remove(left.NodeId);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _log.Warning("Failed to deserialize discovery event [{0}]: {1}", evt.EventType, ex.Message);
        }
    }

    private void OnGetMembers()
    {
        var ttlCutoff = DateTimeOffset.UtcNow - _settings.HeartbeatTtl;

        var alive = _members.Values
            .Where(m => m.LastSeen >= ttlCutoff)
            .ToList();

        Sender.Tell(new MembersResult(alive));
    }

    private void OnSubscriptionDropped(SubscriptionDropped msg)
    {
        if (msg.Exception is not null)
            _log.Warning("Discovery stream subscription dropped: {0}", msg.Exception.Message);

        // Resubscribe after a short delay using Timers (single-shot)
        Timers.StartSingleTimer("resubscribe", new ResubscribeCommand(), TimeSpan.FromSeconds(2));
    }

    private void OnResubscribe()
    {
        _log.Info("Resubscribing to discovery stream [{0}]", _settings.StreamName);
        StartSubscription();
    }

    private sealed record ResubscribeCommand;

    // --- Subscription ---

    private void StartSubscription()
    {
        _subscriptionCts?.Cancel();
        _subscriptionCts?.Dispose();
        _subscriptionCts = new CancellationTokenSource();

        var self = Self;
        var streamName = _settings.StreamName;
        var ct = _subscriptionCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                // SubscribeToStream creates a catch-up subscription from the start.
                // If the stream doesn't exist yet, it will wait until events arrive.
                var subscription = _client.SubscribeToStream(streamName, FromStream.Start, cancellationToken: ct);

                await foreach (var message in subscription.Messages.WithCancellation(ct))
                {
                    if (message is StreamMessage.Event(var resolvedEvent))
                    {
                        self.Tell(new EventReceived(
                            resolvedEvent.Event.EventType,
                            resolvedEvent.Event.Data.ToArray()));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
            catch (Exception ex)
            {
                self.Tell(new SubscriptionDropped(ex));
            }
        }, ct);
    }
}
