using Necronet.Contracts;

namespace Necronet.ChatServer.Services;

/// <summary>
/// The persistence seam for sending a message. The hub depends on this
/// port, NOT on how a message is stored — so the current
/// Supabase-RPC-backed implementation can later be swapped for native
/// service logic (the "thick DB → service tier" migration) without
/// touching the hub.
///
/// Implementations OWN the durable write and return the persisted,
/// authoritative <see cref="ChatMessageDto"/>; the hub is responsible
/// only for fanning that DTO out to the channel group.
///
/// The service authenticates as itself and writes on behalf of
/// <paramref name="senderAccountId"/> (the connection's JWT-validated
/// user) — it never impersonates the user at the data layer.
/// </summary>
public interface IChatMessageService
{
    /// <summary>
    /// Persist a message authored by <paramref name="senderAccountId"/>
    /// and return the stored row as a DTO ready to broadcast. Throws
    /// <see cref="ChatMessageRejectedException"/> when the write is
    /// refused by a business rule (slowmode, muted, locked, not a
    /// member, …).
    /// </summary>
    Task<ChatMessageDto> SendAsync(
        Guid senderAccountId,
        SendMessageRequest request,
        CancellationToken ct);

    /// <summary>
    /// Persist a 1:1 whisper/DM authored by
    /// <paramref name="senderAccountId"/> to
    /// <see cref="SendDmRequest.RecipientId"/>, resolving (creating on
    /// first contact) the DM channel. Enforces the DM-specific gates
    /// (whisper-privacy + mutual-friend), NOT channel-message gates.
    /// Returns the stored row (its <see cref="ChatMessageDto.ChannelId"/>
    /// is the resolved DM channel, which the hub fans out to).
    /// </summary>
    Task<ChatMessageDto> SendDmAsync(
        Guid senderAccountId,
        SendDmRequest request,
        CancellationToken ct);

    /// <summary>
    /// The id of the EXISTING DM channel between <paramref name="userId"/>
    /// and <paramref name="peerId"/>, or null if none has been created
    /// yet. Does not create one. Used so the hub can subscribe a
    /// connection to its DM group without the client knowing the channel
    /// id; only returns a channel the caller is a participant of.
    /// </summary>
    Task<Guid?> ResolveDmChannelAsync(
        Guid userId,
        Guid peerId,
        CancellationToken ct);
}

/// <summary>
/// A send was refused by a business rule (the kind a user should see:
/// "you are muted", "slowmode: wait 5 more seconds"). The hub surfaces
/// <see cref="Exception.Message"/> to the caller as a HubException.
/// Distinct from infrastructure failures, which should NOT leak their
/// detail to clients.
/// </summary>
public sealed class ChatMessageRejectedException : Exception
{
    public ChatMessageRejectedException(string message) : base(message) { }
}
