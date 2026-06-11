namespace Necronet.Contracts;

/// <summary>
/// Canonical SignalR group-name builders. Both the chat server (which
/// adds connections to groups) and the game server (which will publish
/// into them) MUST derive group names from here so they never drift.
///
/// A SignalR "group" is just a named set of connections — it maps 1:1
/// onto a logical chat channel. A connection joins <c>channel:{id}</c>
/// and every broadcast to that group fans out to it. This is the same
/// "subscribe to a channel, get messages pushed" model Discord/Twitch
/// use; the group is the subscription.
/// </summary>
public static class ChannelGroups
{
    /// <summary>Group for a single channel (trade, world, a guild, a DM, …).</summary>
    public static string ForChannel(Guid channelId) => $"channel:{channelId}";

    /// <summary>
    /// Group for a world zone's proximity feed. Populated later, once the
    /// game server starts publishing spatial chat — kept here so the name
    /// is settled before either side needs it.
    /// </summary>
    public static string ForZone(long zoneId) => $"zone:{zoneId}";
}
