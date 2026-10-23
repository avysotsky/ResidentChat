using System.Globalization;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace ResidentChat.Api.Ingress;

/// <summary>
/// RC-21 LAB-only durable cursor. NOT registered as a hosted service.
/// Handles only one explicitly named bot+tenant consumer.
/// Explicit bootstrap only after an owner audits the earliest pending update.
/// PostgreSQL evidence of classified intake is mandatory before any cursor
/// advancement. A metadata-only blocker NEVER permits Telegram ACK.
/// </summary>
public sealed class PostgresTelegramCursorStore(string connectionString)
{
    public const string LabConsumer = "residentchat_od_lab_bot_alpha";

    public enum BlockReason : short
    {
        UnsupportedService = 1,
        Malformed = 2,
        ForeignOrUnmapped = 3
    }

    private static void Validate(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant scope is mandatory.", nameof(tenantId));
    }

    private async Task<(NpgsqlConnection Db, NpgsqlTransaction Tx)> OpenAsync(
        Guid tenantId, CancellationToken ct)
    {
        Validate(tenantId);
        var db = new NpgsqlConnection(connectionString);
        try
        {
            await db.OpenAsync(ct);
            var tx = await db.BeginTransactionAsync(ct);
            try
            {
                await using var scope = new NpgsqlCommand(
                    "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx);
                scope.Parameters.AddWithValue("tenant", NpgsqlDbType.Text,
                    tenantId.ToString("D", CultureInfo.InvariantCulture));
                await scope.ExecuteNonQueryAsync(ct);
                return (db, tx);
            }
            catch
            {
                await tx.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    public async Task<long?> ReadAsync(Guid tenantId, CancellationToken ct)
    {
        var (db, tx) = await OpenAsync(tenantId, ct);
        await using (db)
        await using (tx)
        {
            await using var sql = new NpgsqlCommand("""
                SELECT next_offset FROM residentchat.telegram_poll_cursors
                WHERE tenant_id = @tenant AND consumer_key = @consumer
                """, db, tx);
            sql.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            sql.Parameters.AddWithValue("consumer", NpgsqlDbType.Varchar, LabConsumer);
            var value = await sql.ExecuteScalarAsync(ct);
            await tx.CommitAsync(ct);
            return value is long next ? next : null;
        }
    }

    /// <summary>
    /// Explicit, one-time cursor bootstrap. This writes a SQL marker but
    /// DOES NOT call Telegram getUpdates or confirm any pending update.
    /// Must use the earliest verified pending ID; cannot overwrite a cursor.
    /// </summary>
    public async Task<long> InitializeAtAsync(Guid tenantId,
        long earliestPendingUpdate, CancellationToken ct)
    {
        if (earliestPendingUpdate <= 0 || earliestPendingUpdate == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(earliestPendingUpdate));

        var (db, tx) = await OpenAsync(tenantId, ct);
        await using (db)
        await using (tx)
        {
            await using (var add = new NpgsqlCommand("""
                INSERT INTO residentchat.telegram_poll_cursors(
                    tenant_id, consumer_key, next_offset)
                VALUES (@tenant, @consumer, @initial)
                ON CONFLICT (consumer_key) DO NOTHING
                """, db, tx))
            {
                add.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
                add.Parameters.AddWithValue("consumer", NpgsqlDbType.Varchar, LabConsumer);
                add.Parameters.AddWithValue("initial", NpgsqlDbType.Bigint, earliestPendingUpdate);
                await add.ExecuteNonQueryAsync(ct);
            }
            await using var read = new NpgsqlCommand("""
                SELECT next_offset FROM residentchat.telegram_poll_cursors
                WHERE tenant_id = @tenant AND consumer_key = @consumer
                """, db, tx);
            read.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            read.Parameters.AddWithValue("consumer", NpgsqlDbType.Varchar, LabConsumer);
            var result = await read.ExecuteScalarAsync(ct);
            if (result is not long stored)
                throw new InvalidOperationException("Cursor belongs to another tenant.");
            await tx.CommitAsync(ct);
            return stored;
        }
    }

    /// <summary>
    /// Persist only event category and update ID, never service-event JSON.
    /// Does NOT change cursor or authorize Telegram ACK.
    /// </summary>
    public async Task<bool> RecordBlockedAsync(Guid tenantId,
        long updateId, BlockReason reason, CancellationToken ct)
    {
        if (updateId < 0 || updateId == long.MaxValue
            || !Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(updateId));
        var (db, tx) = await OpenAsync(tenantId, ct);
        await using (db)
        await using (tx)
        {
            await using var sql = new NpgsqlCommand("""
                INSERT INTO residentchat.telegram_poll_blockers(
                    tenant_id, consumer_key, update_id, reason)
                VALUES (@tenant, @consumer, @id, @reason)
                ON CONFLICT (tenant_id, consumer_key, update_id) DO NOTHING
                """, db, tx);
            sql.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            sql.Parameters.AddWithValue("consumer", NpgsqlDbType.Varchar, LabConsumer);
            sql.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, updateId);
            sql.Parameters.AddWithValue("reason", NpgsqlDbType.Smallint, (short)reason);
            var affected = await sql.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return affected == 1;
        }
    }

    /// <summary>
    /// Compare-and-swap strictly one update at a time; never jump over an
    /// unclassified/service update. Requires matching durable intake plus
    /// pending proposal or unexpired AES-GCM review and NO blocker row.
    /// Should only run after classified pipeline's transaction committed.
    /// Caller is still forbidden to start real polling until full live
    /// operational review and durable storage rollout are complete.
    /// </summary>
    public async Task<bool> TryAdvanceAfterAdmissionAsync(Guid tenantId,
        long expectedOffset, long admittedUpdateId, CancellationToken ct)
    {
        if (expectedOffset < 0 || admittedUpdateId != expectedOffset
            || admittedUpdateId == long.MaxValue)
            return false;
        var (db, tx) = await OpenAsync(tenantId, ct);
        await using (db)
        await using (tx)
        {
            await using var sql = new NpgsqlCommand("""
                UPDATE residentchat.telegram_poll_cursors c
                SET next_offset = @next, modified_at = now()
                WHERE c.tenant_id = @tenant
                  AND c.consumer_key = @consumer
                  AND c.next_offset = @expected
                  AND NOT EXISTS (
                    SELECT 1 FROM residentchat.telegram_poll_blockers b
                    WHERE b.tenant_id = c.tenant_id
                      AND b.consumer_key = c.consumer_key
                      AND b.update_id = @expected
                  )
                  AND EXISTS (
                    SELECT 1 FROM residentchat.telegram_intake i
                    WHERE i.tenant_id = c.tenant_id AND i.update_id = @expected
                      AND (
                        EXISTS (
                          SELECT 1 FROM residentchat.operator_proposals p
                          WHERE p.tenant_id = i.tenant_id
                            AND p.source_update_id = i.update_id)
                        OR EXISTS (
                          SELECT 1 FROM residentchat.telegram_reviews r
                          WHERE r.tenant_id = i.tenant_id
                            AND r.update_id = i.update_id
                            AND r.expires_at > now())
                      )
                  )
                """, db, tx);
            sql.Parameters.AddWithValue("next", NpgsqlDbType.Bigint, admittedUpdateId + 1);
            sql.Parameters.AddWithValue("expected", NpgsqlDbType.Bigint, expectedOffset);
            sql.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            sql.Parameters.AddWithValue("consumer", NpgsqlDbType.Varchar, LabConsumer);
            bool success = await sql.ExecuteNonQueryAsync(ct) == 1;
            await tx.CommitAsync(ct);
            return success;
        }
    }
}
