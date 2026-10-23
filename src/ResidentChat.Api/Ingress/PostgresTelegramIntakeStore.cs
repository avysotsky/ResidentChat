using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using ResidentChat.Api.Operator;
using ResidentChat.Core.Ingress;

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
        var ready = enabled && valid && configured.Length > 0
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
    public async Task<bool> AcceptAsync(
        Guid tenantId, long telegramUpdateId, long groupId, CancellationToken ct)
    {
        if (!config.Ready || tenantId == Guid.Empty || telegramUpdateId < 0
            || !config.TryResolveTenant(groupId, out var mappedTenant)
            || mappedTenant != tenantId)
            throw new InvalidOperationException("Durable ingress scope is not configured.");

        var key = Encoding.UTF8.GetBytes(config.HmacKey);
        var input = Encoding.ASCII.GetBytes(
            groupId.ToString(CultureInfo.InvariantCulture));
        string groupHash;
        try
        {
            groupHash = Convert.ToHexString(HMACSHA256.HashData(key, input));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(input);
        }

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
            insert.Parameters.AddWithValue("chat_hash", NpgsqlDbType.Char, groupHash);
            added = await insert.ExecuteScalarAsync(ct) is not null;
        }

        await tx.CommitAsync(ct);
        return added;
    }
}
