namespace Necronet.Contracts;

/// <summary>
/// A lightweight realtime "something happened" poke delivered to a
/// specific user (<c>Clients.User(...)</c>). Deliberately generic and
/// id-only: the client uses <see cref="Type"/> to decide which small
/// list to refetch (signal-to-refetch), rather than carrying full
/// payloads. New event kinds reuse this shape instead of adding a
/// bespoke client method each time.
///
/// Origin: the DB emits a NOTIFY on write, the chat server's
/// chat-events listener resolves recipients and pushes this.
/// </summary>
public sealed record NotificationDto
{
    /// <summary>Event kind, e.g. <c>channel_join_request</c>.</summary>
    public required string Type { get; init; }

    /// <summary>The channel involved, when the event is channel-scoped.</summary>
    public Guid? ChannelId { get; init; }

    /// <summary>The primary resource id (e.g. the join-request id).</summary>
    public Guid? ResourceId { get; init; }

    /// <summary>Who triggered it (e.g. the requesting account), if any.</summary>
    public Guid? ActorId { get; init; }
}
