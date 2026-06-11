using Microsoft.AspNetCore.SignalR;

namespace Necronet.ChatServer.Realtime;

/// <summary>
/// Tells SignalR which claim identifies a user, so
/// <c>Clients.User(id)</c> and <c>Context.UserIdentifier</c> resolve to
/// the Supabase account id. Supabase puts the account id in the JWT's
/// <c>sub</c> claim; we keep <c>MapInboundClaims = false</c> on the JWT
/// handler so the claim type stays the literal "sub" rather than being
/// remapped to the long ClaimTypes.NameIdentifier URI.
/// </summary>
public sealed class SubUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
        => connection.User?.FindFirst("sub")?.Value;
}
