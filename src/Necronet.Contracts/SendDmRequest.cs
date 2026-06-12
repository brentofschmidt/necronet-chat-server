namespace Necronet.Contracts;

/// <summary>
/// Payload for the hub's <c>SendDm</c> method — post a 1:1 whisper/DM to
/// another account. DMs are peer-keyed on the wire (the client never
/// needs the underlying channel id); the server resolves/creates the DM
/// channel and enforces the whisper-privacy + mutual-friend gates via
/// the <c>send_dm</c> RPC, which are DIFFERENT from channel-message
/// gates — hence a distinct request from <see cref="SendMessageRequest"/>.
///
/// The sender is the connection's JWT-validated user, never carried
/// here. <see cref="ClientNonce"/> is the idempotency key + optimistic
/// reconciliation handle.
/// </summary>
public sealed record SendDmRequest
{
    public required Guid RecipientId { get; init; }
    public required string Body { get; init; }
    public required string ClientNonce { get; init; }
}
