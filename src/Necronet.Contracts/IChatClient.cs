namespace Necronet.Contracts;

/// <summary>
/// The methods the SERVER can invoke on a connected CLIENT — i.e. the
/// events a client receives. Using a strongly-typed hub
/// (<c>Hub&lt;IChatClient&gt;</c>) means the server calls
/// <c>Clients.Group(...).ReceiveMessage(dto)</c> with compile-time
/// checking instead of a magic string method name. The web client's
/// SignalR handlers must register for these exact names.
/// </summary>
public interface IChatClient
{
    /// <summary>A new message arrived in a channel the client is subscribed to.</summary>
    Task ReceiveMessage(ChatMessageDto message);

    /// <summary>An existing message was edited.</summary>
    Task MessageEdited(ChatMessageDto message);

    /// <summary>A message was deleted (by author or a moderator).</summary>
    Task MessageDeleted(Guid channelId, Guid messageId);
}
