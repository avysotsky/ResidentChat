using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using ResidentChat.Api.Operator;
using ResidentChat.Core.Ingress;
using ResidentChat.Core.Demo;
using ResidentChat.Core.Tickets;

namespace ResidentChat.Api.Ingress;

/// <summary>
/// Opt-in durable Telegram admission, disabled by default. The allowlist
/// maps actual Telegram group IDs to trusted tenant IDs in configuration,
/// NEVER from Telegram-provided message content or LLM classifications.
/// </summary>
public sealed class DurableTelegramIntakeOptions
{
    private readonly IReadOnlyDictionary<long, Guid> _groups;

    private DurableTelegramIntakeOptions(
        bool enabled, bool ready, string hmacKey, Dictionary<long, Guid> groups)
    {
        Enabled = enabled;
        Ready = ready;
        HmacKey = hmacKey;
        _groups = groups;
    }

    public bool Enabled { get; }
    public bool Ready { get; }
    internal string HmacKey { get; }

    public bool TryResolveTenant(long chatId, out Guid tenantId) =>
        Ready && _groups.TryGetValue(chatId, out tenantId)
            ? tenantId != Guid.Empty
            : ResolveMissing(out tenantId);

    private static bool ResolveMissing(out Guid tenantId)
    {
        tenantId = Guid.Empty;
        return false;
    }

    public static DurableTelegramIntakeOptions Create(
        IConfiguration config, TelegramIngressPolicy telegram, OperatorOptions op)
    {
        var section = config.GetSection("ResidentChat:DurableIntake");
        var enabled = section.GetValue<bool>("Enabled");
        // This interim mode intentionally persists only metadata. It may ACK
        // fictional test-group messages, but must never be mistaken for the
        // future lossless resident-processing pipeline.
        var metadataTestOnly = string.Equals(section["Mode"],
            "metadata-only-test-groups", StringComparison.Ordinal);
        var hmacKey = section["ChatHashHmacKey"] ?? "";
        var groups = new Dictionary<long, Guid>();
        var valid = true;
        var configured = section.GetSection("Groups").GetChildren().ToArray();
        foreach (var child in configured)
        {
            if (!long.TryParse(child["ChatId"], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var chatId)
                || chatId >= 0
                || !Guid.TryParse(child["TenantId"], out var tenantId)
                || tenantId == Guid.Empty || !op.AllowedTenantIds.Contains(tenantId)
                || !groups.TryAdd(chatId, tenantId))
                valid = false;
        }

        // No partial admission when one expected group lacks a tenant mapping
        // or when the operator database, signing/audit config, or webhook is bad.
        var ready = enabled && metadataTestOnly && valid && configured.Length > 0
            && telegram.Ready && op.Ready && telegram.MatchesGroupSet(groups.Keys)
            && hmacKey.Length >= 32 && hmacKey.Length <= 512
            && Encoding.UTF8.GetByteCount(hmacKey) >= 32
            && !string.Equals(hmacKey, op.JwtSigningKey, StringComparison.Ordinal)
            && !string.Equals(hmacKey, op.AuditHmacKey, StringComparison.Ordinal);
        return new DurableTelegramIntakeOptions(enabled, ready, hmacKey, groups);
    }
}

/// <summary>
/// Stores ONLY update IDs, tenant IDs, and an HMAC of the group ID.
/// Never stores message text, people, phone numbers or Telegram bot tokens.
/// The INSERT is protected by PostgreSQL RLS and committed before replying.
/// </summary>
public sealed class PostgresTelegramIntakeStore(
    string connectionString, DurableTelegramIntakeOptions config)
{
    /// <summary>
    /// Classification already completed locally; commit the admitted update
    /// and actionable human-approval proposal in ONE database transaction.
    /// If either insert fails, NO Telegram acknowledgement may occur.
    /// </summary>
    public async Task<bool> AcceptWithProposalAsync(
        Guid tenantId, long updateId, long groupId, DemoTicketProposal proposal,
        CancellationToken ct)
    {
        if (!config.Ready || tenantId == Guid.Empty || updateId < 0
            || !config.TryResolveTenant(groupId, out var trustedTenant)
            || trustedTenant != tenantId || proposal.AuthorizedTenantId != tenantId
            || !proposal.RequiresHumanApproval
            || !TicketRules.IsCategory(proposal.Category)
            || !TicketRules.IsPriority(proposal.Priority))
            throw new InvalidOperationException("Classified intake lacks trusted tenant scope.");

        var groupHash = HashChat(groupId);
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var set = new NpgsqlCommand(
            "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx))
        {
            set.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenantId.ToString("D"));
            await set.ExecuteNonQueryAsync(ct);
        }

        bool added;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO residentchat.telegram_intake
                (tenant_id, update_id, chat_hash, state)
            VALUES (@tenant, @update, @hash, 1)
            ON CONFLICT (tenant_id, update_id) DO NOTHING
            RETURNING update_id
            """, db, tx))
        {
            insert.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            insert.Parameters.AddWithValue("update", NpgsqlDbType.Bigint, updateId);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Text, groupHash);
            added = await insert.ExecuteScalarAsync(ct) is not null;
        }

        if (added)
        {
            await using var create = new NpgsqlCommand("""
                INSERT INTO residentchat.operator_proposals
                    (tenant_id, proposal_id, source_update_id,
                     category, priority, state)
                VALUES (@tenant, @id, @update, @category, @priority, 1)
                """, db, tx);
            create.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            create.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, Guid.NewGuid());
            create.Parameters.AddWithValue("update", NpgsqlDbType.Bigint, updateId);
            create.Parameters.AddWithValue("category", NpgsqlDbType.Smallint,
                (short)proposal.Category);
            create.Parameters.AddWithValue("priority", NpgsqlDbType.Smallint,
                (short)proposal.Priority);
            await create.ExecuteNonQueryAsync(ct);
        }
        else
        {
            // Do NOT acknowledge an older metadata-only update that never
            // yielded a proposal, or a conflicting update from another group.
            await using var verify = new NpgsqlCommand("""
                SELECT i.chat_hash
                FROM residentchat.telegram_intake i
                JOIN residentchat.operator_proposals p
                  ON p.tenant_id = i.tenant_id
                 AND p.source_update_id = i.update_id
                WHERE i.tenant_id = @tenant AND i.update_id = @update
                """, db, tx);
            verify.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            verify.Parameters.AddWithValue("update", NpgsqlDbType.Bigint, updateId);
            var previouslyCommittedHash = (string?)await verify.ExecuteScalarAsync(ct);
            if (!string.Equals(previouslyCommittedHash?.TrimEnd(), groupHash,
                StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Duplicate Telegram update has no matching durable proposal.");
        }

        await tx.CommitAsync(ct);
        return added;
    }

    private string HashChat(long chatId)
    {
        var key = Encoding.UTF8.GetBytes(config.HmacKey);
        var input = Encoding.ASCII.GetBytes(
            chatId.ToString(CultureInfo.InvariantCulture));
        try { return Convert.ToHexString(HMACSHA256.HashData(key, input)); }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    // Server-limited, signed-operator readback. No raw group ID/chat hash
    // or message bytes are exposed to the React operator UI.
    public async Task<IReadOnlyList<TelegramIntakeSnapshot>> ListRecentAsync(
        Guid tenantId, CancellationToken ct)
    {
        if (!config.Ready || tenantId == Guid.Empty)
            throw new InvalidOperationException("Durable intake is not ready.");

        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var set = new NpgsqlCommand(
            "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx))
        {
            set.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenantId.ToString("D"));
            await set.ExecuteNonQueryAsync(ct);
        }

        var snapshots = new List<TelegramIntakeSnapshot>();
        await using (var query = new NpgsqlCommand("""
            SELECT update_id, state, received_at
            FROM residentchat.telegram_intake
            WHERE tenant_id = @tenant
            ORDER BY received_at DESC, update_id DESC
            LIMIT 50
            """, db, tx))
        {
            query.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                snapshots.Add(new TelegramIntakeSnapshot(
                    reader.GetInt64(0), reader.GetInt16(1),
                    new DateTimeOffset(reader.GetDateTime(2))));
        }
        await tx.CommitAsync(ct);
        return snapshots;
    }

    public async Task<bool> AcceptAsync(
        Guid tenantId, long telegramUpdateId, long groupId, CancellationToken ct)
    {
        if (!config.Ready || tenantId == Guid.Empty || telegramUpdateId < 0
            || !config.TryResolveTenant(groupId, out var mappedTenant)
            || mappedTenant != tenantId)
            throw new InvalidOperationException("Durable ingress scope is not configured.");

        var groupHash = HashChat(groupId);

        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var set = new NpgsqlCommand(
            "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx))
        {
            set.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenantId.ToString("D"));
            await set.ExecuteNonQueryAsync(ct);
        }

        bool added;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO residentchat.telegram_intake
                (tenant_id, update_id, chat_hash, state)
            VALUES (@tenant, @update, @chat_hash, 1)
            ON CONFLICT (tenant_id, update_id) DO NOTHING
            RETURNING update_id
            """, db, tx))
        {
            insert.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            insert.Parameters.AddWithValue("update", NpgsqlDbType.Bigint, telegramUpdateId);
            insert.Parameters.AddWithValue("chat_hash", NpgsqlDbType.Text, groupHash);
            added = await insert.ExecuteScalarAsync(ct) is not null;
        }

        await tx.CommitAsync(ct);
        return added;
    }
}

public sealed record TelegramIntakeSnapshot(
    long UpdateId, short State, DateTimeOffset ReceivedAt);
