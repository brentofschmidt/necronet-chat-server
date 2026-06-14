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

    /// <summary>
    /// A realtime notification poke for this user (a new join request to
    /// moderate, a friend request, an approval, …). Originates from the
    /// DB-fed chat-events watchdog, not from a message send. The client
    /// keys off <see cref="NotificationDto.Type"/> to refetch the
    /// relevant list.
    /// </summary>
    Task ReceiveNotification(NotificationDto notification);
}
