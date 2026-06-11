using Npgsql;

namespace Necronet.ChatServer.Services;

/// <summary>
/// Membership check backed by the same Supabase Postgres the web app
/// uses. Reads <c>social.channel_subscriptions</c> directly — the row
/// that the web app's join/accept RPCs create. Connects as the database
/// user in the connection string (typically a least-privilege role, NOT
/// service_role over PostgREST); this bypasses RLS by design because it
/// runs server-side and performs its own authorization with the
/// connection's authenticated user id.
/// </summary>
public sealed class NpgsqlChannelMembershipService : IChannelMembershipService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<NpgsqlChannelMembershipService> _logger;

    public NpgsqlChannelMembershipService(
        NpgsqlDataSource dataSource,
        ILogger<NpgsqlChannelMembershipService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<bool> IsMemberAsync(Guid userId, Guid channelId, CancellationToken ct)
    {
        const string sql = """
            select exists (
                select 1
                  from social.channel_subscriptions
                 where account_id = @user_id
                   and channel_id = @channel_id
            )
            """;

        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue("user_id", userId);
            cmd.Parameters.AddWithValue("channel_id", channelId);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is true;
        }
        catch (Exception ex)
        {
            // Fail closed: if we can't confirm membership, deny the join.
            _logger.LogError(ex,
                "Membership check failed for user {UserId} channel {ChannelId}",
                userId, channelId);
            return false;
        }
    }
}
