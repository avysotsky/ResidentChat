using System.Data;
using Npgsql;
using NpgsqlTypes;
using ResidentChat.Core.Tickets;

namespace ResidentChat.Api.Operator;

/// <summary>
/// Tenant scope is applied twice: every statement filters by tenant and PostgreSQL RLS
/// uses a transaction-local tenant ID. The runtime DB role MUST NOT own the tables,
/// be a superuser, or have BYPASSRLS. Never write resident chat text here.
/// </summary>
public sealed class PostgresTicketStore(string connectionString)
{
    public async Task<TicketSnapshot> CreateAsync(
        Guid tenantId, TicketCategory category, TicketPriority priority,
        string actorHash, CancellationToken ct)
    {
        var ticketId = Guid.NewGuid();
        var initialStatus = priority == TicketPriority.Urgent
            ? TicketStatus.Escalated : TicketStatus.Open;

        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await SetTenantAsync(db, tx, tenantId, ct);

        TicketSnapshot created;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO residentchat.tickets
                (tenant_id, ticket_id, category, priority, status, version)
            VALUES (@tenant, @id, @category, @priority, @status, 1)
            RETURNING ticket_id, tenant_id, category, priority, status,
                      version, created_at, updated_at
            """, db, tx))
        {
            AddTenant(cmd, tenantId);
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, ticketId);
            cmd.Parameters.AddWithValue("category", NpgsqlDbType.Smallint, (short)category);
            cmd.Parameters.AddWithValue("priority", NpgsqlDbType.Smallint, (short)priority);
            cmd.Parameters.AddWithValue("status", NpgsqlDbType.Smallint, (short)initialStatus);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("Ticket creation did not return a row.");
            created = ReadTicket(reader);
        }

        await AppendAuditAsync(db, tx, tenantId, ticketId,
            initialStatus == TicketStatus.Escalated ? "created_escalated" : "created",
            actorHash, ct);
        await tx.CommitAsync(ct);
        return created;
    }

    public async Task<TicketSnapshot?> GetAsync(Guid tenantId, Guid ticketId, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await SetTenantAsync(db, tx, tenantId, ct);
        var result = await GetLockedAsync(db, tx, tenantId, ticketId, false, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<TicketMutationResult> TransitionAsync(
        Guid tenantId, Guid ticketId, int expectedVersion, TicketStatus target,
        bool isSupervisor, string actorHash, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await SetTenantAsync(db, tx, tenantId, ct);

        var current = await GetLockedAsync(db, tx, tenantId, ticketId, true, ct);
        if (current is null)
            return new(TicketMutationOutcome.NotFound);
        if (current.Version != expectedVersion ||
            !TicketRules.CanTransition(current.Status, target, isSupervisor))
            return new(TicketMutationOutcome.Conflict);

        TicketSnapshot? updated;
        await using (var cmd = new NpgsqlCommand("""
            UPDATE residentchat.tickets
            SET status = @status, version = version + 1, updated_at = now()
            WHERE tenant_id = @tenant AND ticket_id = @id AND version = @version
            RETURNING ticket_id, tenant_id, category, priority, status,
                      version, created_at, updated_at
            """, db, tx))
        {
            AddTenant(cmd, tenantId);
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, ticketId);
            cmd.Parameters.AddWithValue("version", NpgsqlDbType.Integer, expectedVersion);
            cmd.Parameters.AddWithValue("status", NpgsqlDbType.Smallint, (short)target);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            updated = await reader.ReadAsync(ct) ? ReadTicket(reader) : null;
        }
        if (updated is null)
            return new(TicketMutationOutcome.Conflict);

        await AppendAuditAsync(db, tx, tenantId, ticketId,
            target == TicketStatus.Escalated ? "escalated" : "resolved",
            actorHash, ct);
        await tx.CommitAsync(ct);
        return new(TicketMutationOutcome.Updated, updated);
    }

    private static async Task SetTenantAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx);
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenantId.ToString("D"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<TicketSnapshot?> GetLockedAsync(
        NpgsqlConnection db, NpgsqlTransaction tx,
        Guid tenantId, Guid ticketId, bool forUpdate, CancellationToken ct)
    {
        var sql = """
            SELECT ticket_id, tenant_id, category, priority, status,
                   version, created_at, updated_at
            FROM residentchat.tickets
            WHERE tenant_id = @tenant AND ticket_id = @id
            """ + (forUpdate ? " FOR UPDATE" : "");
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        AddTenant(cmd, tenantId);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, ticketId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTicket(reader) : null;
    }

    private static TicketSnapshot ReadTicket(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        (TicketCategory)reader.GetInt16(2),
        (TicketPriority)reader.GetInt16(3),
        (TicketStatus)reader.GetInt16(4),
        reader.GetInt32(5),
        new DateTimeOffset(reader.GetDateTime(6)),
        new DateTimeOffset(reader.GetDateTime(7)));

    private static void AddTenant(NpgsqlCommand cmd, Guid tenantId) =>
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);

    private static async Task AppendAuditAsync(
        NpgsqlConnection db, NpgsqlTransaction tx,
        Guid tenantId, Guid ticketId, string action, string actorHash, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO residentchat.ticket_audit
                (tenant_id, ticket_id, action, actor_hash)
            VALUES (@tenant, @id, @action, @actor)
            """, db, tx);
        AddTenant(cmd, tenantId);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, ticketId);
        cmd.Parameters.AddWithValue("action", NpgsqlDbType.Text, action);
        cmd.Parameters.AddWithValue("actor", NpgsqlDbType.Text, actorHash);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
