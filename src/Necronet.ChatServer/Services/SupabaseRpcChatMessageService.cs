using System.Text.Json;
using Necronet.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Necronet.ChatServer.Services;

/// <summary>
/// <see cref="IChatMessageService"/> backed by the Supabase
/// <c>social.send_channel_message</c> RPC. The service connects with
/// its own DB identity and passes the acting user as
/// <c>p_sender_account_id</c> (see migration 0442) — it does NOT
/// impersonate the user at the session level. All the send gates
/// (dedup, rate limit, lock/ban/mute/slowmode, entity + mention
/// validation) live in the RPC and run for the resolved sender, so
/// there is exactly one copy of that logic, shared with the browser
/// path.
///
/// This is the implementation we expect to replace if/when the send
/// logic moves into this service (the "service tier" migration); the
/// hub only knows the <see cref="IChatMessageService"/> port.
/// </summary>
public sealed class SupabaseRpcChatMessageService : IChatMessageService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<SupabaseRpcChatMessageService> _logger;

    // SQLSTATEs the RPC raises for business-rule refusals the user
    // should see. 42501 = insufficient_privilege (muted/locked/banned/
    // slowmode/not-a-member); 22023 = invalid_parameter_value (empty
    // body, bad entity, …). Anything else is an infra fault.
    private static readonly HashSet<string> UserFacingSqlStates =
        new() { "42501", "22023" };

    // send_dm raises its gate messages via plain `raise exception` with
    // no errcode → SQLSTATE P0001. Those texts ("you can only message
    // mutual friends", …) are meant for the user, so the DM path treats
    // P0001 as user-facing too.
    private static readonly HashSet<string> DmUserFacingSqlStates =
        new() { "42501", "22023", "P0001" };

    public SupabaseRpcChatMessageService(
        NpgsqlDataSource dataSource,
        ILogger<SupabaseRpcChatMessageService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<ChatMessageDto> SendAsync(
        Guid senderAccountId,
        SendMessageRequest request,
        CancellationToken ct)
    {
        var entitiesJson = JsonSerializer.Serialize(
            request.Entities.Select(e => new
            {
                slot = e.Slot,
                kind = e.Kind,
                ref_id = e.RefId,
            }));

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        Guid messageId;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                select social.send_channel_message(
                    @p_channel_id, @p_body, @p_client_nonce,
                    @p_parent_message_id, @p_entities, @p_sender_account_id)
                """;
            cmd.Parameters.AddWithValue("p_channel_id", request.ChannelId);
            cmd.Parameters.AddWithValue("p_body", request.Body);
            cmd.Parameters.AddWithValue("p_client_nonce", request.ClientNonce);
            cmd.Parameters.AddWithValue(
                "p_parent_message_id",
                (object?)request.ParentMessageId ?? DBNull.Value);
            cmd.Parameters.Add(new NpgsqlParameter("p_entities", NpgsqlDbType.Jsonb)
            {
                Value = entitiesJson,
            });
            cmd.Parameters.AddWithValue("p_sender_account_id", senderAccountId);

            var result = await cmd.ExecuteScalarAsync(ct);
            messageId = (Guid)result!;
        }
        catch (PostgresException ex) when (UserFacingSqlStates.Contains(ex.SqlState))
        {
            // A business-rule refusal — safe to show the user verbatim.
            throw new ChatMessageRejectedException(ex.MessageText);
        }

        return await LoadDtoAsync(conn, messageId, ct);
    }

    public async Task<ChatMessageDto> SendDmAsync(
        Guid senderAccountId,
        SendDmRequest request,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        Guid messageId;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                select social.send_dm(
                    @p_recipient_id, @p_body, @p_client_nonce, @p_sender_account_id)
                """;
            cmd.Parameters.AddWithValue("p_recipient_id", request.RecipientId);
            cmd.Parameters.AddWithValue("p_body", request.Body);
            cmd.Parameters.AddWithValue("p_client_nonce", request.ClientNonce);
            cmd.Parameters.AddWithValue("p_sender_account_id", senderAccountId);

            var result = await cmd.ExecuteScalarAsync(ct);
            messageId = (Guid)result!;
        }
        catch (PostgresException ex) when (DmUserFacingSqlStates.Contains(ex.SqlState))
        {
            throw new ChatMessageRejectedException(ex.MessageText);
        }

        return await LoadDtoAsync(conn, messageId, ct);
    }

    public async Task<Guid?> ResolveDmChannelAsync(
        Guid userId, Guid peerId, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand("""
            select c.id
              from social.channels c
              join social.channel_types ct on ct.id = c.channel_type_id
             where ct.slug = 'dm'
               and c.dm_participant_lo = least(@a, @b)
               and c.dm_participant_hi = greatest(@a, @b)
            """);
        cmd.Parameters.AddWithValue("a", userId);
        cmd.Parameters.AddWithValue("b", peerId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is Guid id ? id : null;
    }

    /// <summary>
    /// Read the just-written row back as the lean DTO to broadcast.
    /// Rich population (entity pills, reactions, per-viewer flags) is
    /// still the client's list_channel_messages concern; the push is a
    /// plain row + the nonce for reconciliation.
    /// </summary>
    private static async Task<ChatMessageDto> LoadDtoAsync(
        NpgsqlConnection conn, Guid messageId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select m.id, m.channel_id, m.sender_account_id,
                   u.display_name, m.body, m.sent_at, m.edited_at,
                   m.parent_message_id, m.client_nonce
              from social.messages m
              join accounts.users u on u.id = m.sender_account_id
             where m.id = @id
            """;
        cmd.Parameters.AddWithValue("id", messageId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                $"Message {messageId} vanished immediately after insert.");
        }

        return new ChatMessageDto
        {
            Id = reader.GetGuid(0),
            ChannelId = reader.GetGuid(1),
            SenderAccountId = reader.GetGuid(2),
            SenderDisplayName = reader.IsDBNull(3) ? null : reader.GetString(3),
            Body = reader.GetString(4),
            SentAt = reader.GetFieldValue<DateTimeOffset>(5),
            EditedAt = reader.IsDBNull(6)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(6),
            ParentMessageId = reader.IsDBNull(7) ? null : reader.GetGuid(7),
            ClientNonce = reader.IsDBNull(8) ? null : reader.GetString(8),
        };
    }
}
