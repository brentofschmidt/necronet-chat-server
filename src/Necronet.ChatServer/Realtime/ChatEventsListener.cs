using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Necronet.ChatServer.Hubs;
using Necronet.Contracts;
using Npgsql;

namespace Necronet.ChatServer.Realtime;

/// <summary>
/// The "watchdog": a long-lived background service holding a dedicated
/// Postgres connection that LISTENs on the <c>chat_events</c> channel.
/// When the DB emits a NOTIFY (migration 0445), this resolves the
/// recipients and fans a <see cref="NotificationDto"/> out to them via
/// <c>Clients.User(...)</c>.
///
/// This is how RPC-written events (join requests, friend requests, …)
/// reach the realtime layer without routing their writes through the
/// hub: the database is the single convergence point for every write
/// path, so it's the single notification source. The chat server stays
/// a pure relay for these.
/// </summary>
public sealed class ChatEventsListener : BackgroundService
{
    private const string PgChannel = "chat_events";
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly ILogger<ChatEventsListener> _logger;

    public ChatEventsListener(
        NpgsqlDataSource dataSource,
        IHubContext<ChatHub, IChatClient> hub,
        ILogger<ChatEventsListener> logger)
    {
        _dataSource = dataSource;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reconnect loop: the LISTEN connection must survive drops. Any
        // events that commit while we're disconnected are missed (NOTIFY
        // is fire-and-forget) — acceptable for these low-stakes pokes;
        // the affected list is still correct on the next fetch.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "chat_events listener dropped; reconnecting in {Delay}s",
                    ReconnectDelay.TotalSeconds);
                try { await Task.Delay(ReconnectDelay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        conn.Notification += OnNotification;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"LISTEN {PgChannel}";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        _logger.LogInformation("Listening on Postgres channel '{Channel}'", PgChannel);

        // Block until a notification arrives (or the connection drops /
        // we're cancelled). The Notification event fires during the wait.
        while (!ct.IsCancellationRequested)
        {
            await conn.WaitAsync(ct);
        }
    }

    private void OnNotification(object? sender, NpgsqlNotificationEventArgs e)
    {
        // Dispatch off the wait loop so handler work (which runs its own
        // queries) never blocks the listen connection.
        _ = DispatchAsync(e.Payload);
    }

    private async Task DispatchAsync(string payload)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<ChatEvent>(payload, JsonOpts);
            if (evt is null || string.IsNullOrEmpty(evt.Type)) return;

            switch (evt.Type)
            {
                case "channel_join_request":
                    await NotifyChannelModsAsync(evt);
                    break;
                default:
                    _logger.LogDebug("Ignoring unknown chat_event type '{Type}'", evt.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch chat_event: {Payload}", payload);
        }
    }

    private async Task NotifyChannelModsAsync(ChatEvent evt)
    {
        if (evt.ChannelId is not Guid channelId) return;

        var mods = await ResolveChannelModsAsync(channelId);
        if (mods.Count == 0) return;

        var dto = new NotificationDto
        {
            Type = evt.Type,
            ChannelId = channelId,
            ResourceId = evt.RequestId,
            ActorId = evt.RequesterId,
        };

        foreach (var modId in mods)
        {
            // The requester might also be staff somewhere; never notify
            // them of their own request.
            if (modId == evt.RequesterId) continue;
            await _hub.Clients.User(modId.ToString()).ReceiveNotification(dto);
        }
    }

    private async Task<List<Guid>> ResolveChannelModsAsync(Guid channelId)
    {
        var ids = new List<Guid>();
        await using var cmd = _dataSource.CreateCommand("""
            select s.account_id
              from social.channel_subscriptions s
              join social.channel_subscription_roles r on r.id = s.role_id
             where s.channel_id = @cid
               and r.slug in ('owner', 'moderator')
            """);
        cmd.Parameters.AddWithValue("cid", channelId);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }
        return ids;
    }

    /// <summary>Shape of the JSON payload from pg_notify('chat_events', …).</summary>
    private sealed record ChatEvent
    {
        public string Type { get; init; } = "";
        public Guid? ChannelId { get; init; }
        public Guid? RequestId { get; init; }
        public Guid? RequesterId { get; init; }
    }
}
