namespace Necronet.Contracts;

/// <summary>
/// A chat message as pushed to clients over the wire. Mirrors the core
/// of the web app's <c>ChannelMessage</c> shape.
///
/// v1 deliberately carries only the plain fields. Rich population —
/// resolved entity pills ({0} → @Mortwell), reaction aggregates, and
/// per-viewer block/mute flags — is layered in a later pass. Until
/// then the client's existing <c>list_channel_messages</c> RPC remains
/// the source of truth for the fully-populated render, and a push can
/// be treated as a signal to refetch if richer data is needed.
/// </summary>
public sealed record ChatMessageDto
{
    public required Guid Id { get; init; }
    public required Guid ChannelId { get; init; }
    public required Guid SenderAccountId { get; init; }
    public string? SenderDisplayName { get; init; }
    public required string Body { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public DateTimeOffset? EditedAt { get; init; }

    /// <summary>Set when this message is a reply; null otherwise.</summary>
    public Guid? ParentMessageId { get; init; }
}
