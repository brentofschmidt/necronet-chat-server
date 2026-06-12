namespace Necronet.Contracts;

/// <summary>
/// Payload a browser sends to the hub's <c>SendMessage</c> method to
/// post a message to a channel. The hub persists it (today via the
/// Supabase RPC, behind the <c>IChatMessageService</c> seam) and then
/// fans the resulting <see cref="ChatMessageDto"/> out to the channel
/// group.
///
/// The sender is NOT carried here — it is the connection's
/// JWT-validated user (<c>Context.UserIdentifier</c>), so a client can
/// only ever post as itself. <see cref="ClientNonce"/> is the client's
/// idempotency key, also used to reconcile the optimistic bubble.
/// </summary>
public sealed record SendMessageRequest
{
    public required Guid ChannelId { get; init; }
    public required string Body { get; init; }
    public required string ClientNonce { get; init; }

    /// <summary>Set when posting a reply; null for a top-level message.</summary>
    public Guid? ParentMessageId { get; init; }

    /// <summary>
    /// Entity references backing <c>{N}</c> placeholders in the body
    /// (e.g. @mentions). Empty for a plain message.
    /// </summary>
    public IReadOnlyList<MessageEntityInput> Entities { get; init; }
        = Array.Empty<MessageEntityInput>();
}

/// <summary>
/// One entity reference for a <c>{slot}</c> placeholder — mirrors a row
/// of <c>social.message_entities</c>. <see cref="Kind"/> is a
/// <c>social.linkable_kinds</c> slug (e.g. <c>user</c>).
/// </summary>
public sealed record MessageEntityInput
{
    public required int Slot { get; init; }
    public required string Kind { get; init; }
    public required Guid RefId { get; init; }
}
