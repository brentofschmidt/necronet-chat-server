namespace Necronet.Contracts;

/// <summary>
/// Payload for the chat server's internal publish seam — "fan this
/// message out to everyone subscribed to its channel."
///
/// In v1 this is the single, transport-neutral entry point for getting
/// a message onto the wire. Whoever ends up owning the write path —
/// the web client after a successful send RPC, a Postgres/Supabase
/// webhook, or (later) the game server for spatial chat — calls this
/// same shape. Swapping the transport underneath (HTTP now, a Redis
/// bus later) doesn't change this contract.
/// </summary>
public sealed record PublishMessageRequest
{
    public required ChatMessageDto Message { get; init; }
}
