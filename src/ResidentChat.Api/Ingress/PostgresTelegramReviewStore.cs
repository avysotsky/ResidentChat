using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace ResidentChat.Api.Ingress;

public enum TelegramReviewReason : short
{
    UncertainClassification = 1,
    UrgentHumanSafety = 2,
    NonMaintenance = 3,
    LocalModelUnavailable = 4,
    UnsupportedMessage = 5
}

/// <summary>
/// Review encryption is mandatory before enabling classified Telegram mode.
/// Never provide this 32-byte AES key in Git, command-line arguments or logs.
/// </summary>
public sealed class TelegramReviewOptions
{
    private TelegramReviewOptions(bool enabled, bool ready, byte[] key)
    {
        Enabled = enabled;
        Ready = ready;
        Key = key;
    }

    public bool Enabled { get; }
    public bool Ready { get; }
    internal byte[] Key { get; }

    public static TelegramReviewOptions Create(
        IConfiguration config, DurableTelegramIntakeOptions intake)
    {
        var section = config.GetSection("ResidentChat:TelegramReview");
        var enabled = section.GetValue<bool>("Enabled");
        var mode = string.Equals(section["Mode"],
            "encrypted-test-groups-only", StringComparison.Ordinal);
        var encoded = section["EncryptionKeyBase64"] ?? "";
        var key = Array.Empty<byte>();
        if (encoded.Length == 44)
        {
            try { key = Convert.FromBase64String(encoded); }
            catch (FormatException) { /* fail closed */ }
        }

        var ready = enabled && mode && intake.Ready && key.Length == 32;
        if (!ready && key.Length > 0)
        {
            CryptographicOperations.ZeroMemory(key);
            key = Array.Empty<byte>();
        }
        return new TelegramReviewOptions(enabled, ready, key);
    }
}

public sealed record TelegramReviewSnapshot(
    long UpdateId, TelegramReviewReason Reason, DateTimeOffset ReceivedAt,
    DateTimeOffset ExpiresAt);
public sealed record TelegramReviewDetail(
    long UpdateId, TelegramReviewReason Reason, string Text, DateTimeOffset ExpiresAt);

/// <summary>
/// AES-256-GCM envelope encrypted before PostgreSQL transaction. Tenant ID,
/// update ID and chat fingerprint are authenticated associated data.
/// Decryption does not occur in the intake pipeline, only an authenticated
/// operator's later explicit request (separate route).
/// </summary>
public sealed class PostgresTelegramReviewStore(
    string connectionString, TelegramReviewOptions options,
    DurableTelegramIntakeOptions intake, PostgresTelegramIntakeStore intakeStore)
{
    public static bool IsValidText(string? value) =>
        value is null || value.Length <= 4096;

    public async Task<bool> AcceptForReviewAsync(
        Guid tenantId, long updateId, long chatId, string? text,
        TelegramReviewReason reason, CancellationToken ct)
    {
        if (!options.Ready || tenantId == Guid.Empty || updateId < 0
            || !intake.TryResolveTenant(chatId, out var mappedTenant)
            || mappedTenant != tenantId || !IsValidText(text)
            || !Enum.IsDefined(reason))
            throw new InvalidOperationException("Encrypted review intake not configured.");

        var groupHash = intakeStore.HashChat(chatId);
        var plaintext = Encoding.UTF8.GetBytes(text ?? "");
        var associatedData = AssociatedData(tenantId, updateId, groupHash);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];

        try
        {
            if (plaintext.Length > 16384)
                throw new InvalidOperationException("Encrypted message exceeded limit.");
            using (var aes = new AesGcm(options.Key, 16))
                aes.Encrypt(nonce, plaintext, cipher, tag, associatedData);

            await using var db = new NpgsqlConnection(connectionString);
            await db.OpenAsync(ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            await SetTenantAsync(db, tx, tenantId, ct);
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
                await using var insertReview = new NpgsqlCommand("""
                    INSERT INTO residentchat.telegram_reviews
                        (tenant_id, update_id, reason, nonce, cipher, tag)
                    VALUES (@tenant, @update, @reason, @nonce, @cipher, @tag)
                    """, db, tx);
                insertReview.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
                insertReview.Parameters.AddWithValue("update", NpgsqlDbType.Bigint, updateId);
                insertReview.Parameters.AddWithValue("reason", NpgsqlDbType.Smallint, (short)reason);
                insertReview.Parameters.AddWithValue("nonce", NpgsqlDbType.Bytea, nonce);
                insertReview.Parameters.AddWithValue("cipher", NpgsqlDbType.Bytea, cipher);
                insertReview.Parameters.AddWithValue("tag", NpgsqlDbType.Bytea, tag);
                await insertReview.ExecuteNonQueryAsync(ct);
            }
            else
            {
                // No false duplicate-ACK for prior metadata-only intake or a
                // different group reusing the same update ID.
                await using var verify = new NpgsqlCommand("""
                    SELECT i.chat_hash FROM residentchat.telegram_intake i
                    JOIN residentchat.telegram_reviews r
                      ON r.tenant_id = i.tenant_id AND r.update_id = i.update_id
                    WHERE i.tenant_id = @tenant AND i.update_id = @update
                      AND r.expires_at > now()
                    """, db, tx);
                verify.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
                verify.Parameters.AddWithValue("update", NpgsqlDbType.Bigint, updateId);
                var existing = (string?)await verify.ExecuteScalarAsync(ct);
                if (!string.Equals(existing?.TrimEnd(), groupHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("Inconsistent or expired review update.");
            }

            await tx.CommitAsync(ct);
            return added;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(associatedData);
            CryptographicOperations.ZeroMemory(cipher);
        }
    }

    public async Task<IReadOnlyList<TelegramReviewSnapshot>> ListAsync(
        Guid tenantId, CancellationToken ct)
    {
        EnsureScope(tenantId);
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await SetTenantAsync(db, tx, tenantId, ct);
        var result = new List<TelegramReviewSnapshot>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT update_id, reason, received_at, expires_at
            FROM residentchat.telegram_reviews
            WHERE tenant_id = @tenant AND expires_at > now()
            ORDER BY received_at DESC, update_id DESC LIMIT 50
            """, db, tx))
        {
            cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new TelegramReviewSnapshot(
                    reader.GetInt64(0), (TelegramReviewReason)reader.GetInt16(1),
                    new DateTimeOffset(reader.GetDateTime(2)),
                    new DateTimeOffset(reader.GetDateTime(3))));
        }
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<TelegramReviewDetail?> ReadForOperatorAsync(
        Guid tenantId, long updateId, string actorHash, CancellationToken ct)
    {
        EnsureScope(tenantId);
        if (updateId < 0 || actorHash.Length != 64
            || actorHash.AsSpan().IndexOfAnyExcept("0123456789ABCDEF") >= 0)
            throw new InvalidOperationException("Review read identity invalid.");

        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await SetTenantAsync(db, tx, tenantId, ct);
        byte[] nonce, cipher, tag;
        string hash;
        TelegramReviewReason reason;
        DateTimeOffset expires;
        await using (var query = new NpgsqlCommand("""
            SELECT r.nonce, r.cipher, r.tag, i.chat_hash, r.reason, r.expires_at
            FROM residentchat.telegram_reviews r
            JOIN residentchat.telegram_intake i
              ON i.tenant_id = r.tenant_id AND i.update_id = r.update_id
            WHERE r.tenant_id = @tenant AND r.update_id = @update
              AND r.expires_at > now()
            """, db, tx))
        {
            query.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            query.Parameters.AddWithValue("update", NpgsqlDbType.Bigint, updateId);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            nonce = reader.GetFieldValue<byte[]>(0);
            cipher = reader.GetFieldValue<byte[]>(1);
            tag = reader.GetFieldValue<byte[]>(2);
            hash = reader.GetString(3).TrimEnd();
            reason = (TelegramReviewReason)reader.GetInt16(4);
            expires = new DateTimeOffset(reader.GetDateTime(5));
        }

        var aad = AssociatedData(tenantId, updateId, hash);
        var clear = new byte[cipher.Length];
        string text;
        try
        {
            using (var aes = new AesGcm(options.Key, 16))
                aes.Decrypt(nonce, cipher, tag, clear, aad);
            text = new UTF8Encoding(false, true).GetString(clear);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Encrypted review authentication failed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aad);
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(cipher);
        }

        // One read audit and its return are one scoped transaction. No raw
        // message content, group identity or token is ever sent to an audit.
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO residentchat.telegram_review_reads
                (tenant_id, read_id, update_id, actor_hash)
            VALUES (@tenant, @read, @update, @actor)
            """, db, tx))
        {
            audit.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            audit.Parameters.AddWithValue("read", NpgsqlDbType.Uuid, Guid.NewGuid());
            audit.Parameters.AddWithValue("update", NpgsqlDbType.Bigint, updateId);
            audit.Parameters.AddWithValue("actor", NpgsqlDbType.Text, actorHash);
            await audit.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new TelegramReviewDetail(updateId, reason, text, expires);
    }

    private void EnsureScope(Guid tenantId)
    {
        if (!options.Ready || tenantId == Guid.Empty)
            throw new InvalidOperationException("Encrypted review not ready.");
    }

    private static byte[] AssociatedData(Guid tenantId, long updateId, string chatHash) =>
        Encoding.ASCII.GetBytes(tenantId.ToString("D")
            + ":" + updateId.ToString(CultureInfo.InvariantCulture) + ":" + chatHash);

    private static async Task SetTenantAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenantId.ToString("D"));
        await command.ExecuteNonQueryAsync(ct);
    }
}
