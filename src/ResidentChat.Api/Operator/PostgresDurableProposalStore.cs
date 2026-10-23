using System.Globalization;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using ResidentChat.Core.Tickets;

namespace ResidentChat.Api.Operator;

/// <summary>
/// Isolated laboratory mode for durable proposal/approval testing.
/// Not enabled for live residents. Only mapped admitted test updates are eligible.
/// </summary>
public sealed class DurableProposalOptions
{
    private DurableProposalOptions(bool enabled, bool ready)
    {
        Enabled = enabled;
        Ready = ready;
    }

    public bool Enabled { get; }
    public bool Ready { get; }

    public static DurableProposalOptions Create(IConfiguration config,
        OperatorOptions op, Ingress.DurableTelegramIntakeOptions intake)
    {
        var section = config.GetSection("ResidentChat:DurableProposals");
        var enabled = section.GetValue<bool>("Enabled");
        return new DurableProposalOptions(enabled, enabled && op.Ready && intake.Ready
            && string.Equals(section["Mode"], "synthetic-postgres-only",
                StringComparison.Ordinal));
    }
}

public sealed record DurableProposalItem(
    string CaseId, string Category, string Priority, string State, Guid? TicketId);

public enum DurableProposalChangeResult { Approved, Rejected, Missing, Conflict, Invalid }

/// <summary>
/// One transaction atomically locks a proposal and either creates a ticket,
/// inserts its audit and marks Approved, OR records a Rejected decision.
/// Commit uncertainty is exposed to the API, NEVER automatically retried.
/// </summary>
public sealed class PostgresDurableProposalStore(
    string connectionString, DurableProposalOptions options)
{
    private const string CasePrefix = "RC-";

    public static string CaseId(Guid id) =>
        CasePrefix + id.ToString("N", CultureInfo.InvariantCulture).ToUpperInvariant();

    public static bool TryParseCaseId(string? caseId, out Guid proposalId)
    {
        proposalId = Guid.Empty;
        return caseId is { Length: 35 }
            && caseId.StartsWith(CasePrefix, StringComparison.Ordinal)
            && caseId.AsSpan(3).IndexOfAnyExcept("0123456789ABCDEF") < 0
            && Guid.TryParseExact(caseId[3..], "N", out proposalId)
            && proposalId != Guid.Empty;
    }

    /// <summary>Trustworthy test classifier only: no raw text is persisted.
    /// Never expose as unauthenticated or arbitrary HTTP create endpoint.</summary>
    public async Task<Guid?> CreateFromClassifiedAsync(Guid tenantId,
        long admittedUpdateId, TicketCategory category, TicketPriority priority,
        CancellationToken ct)
    {
        ValidateTenant(tenantId);
        if (admittedUpdateId < 0 || !TicketRules.IsCategory(category)
            || !TicketRules.IsPriority(priority))
            throw new ArgumentException("Invalid classified proposal.");

        var id = Guid.NewGuid();
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await SetTenantAsync(db, tx, tenantId, ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO residentchat.operator_proposals
                (tenant_id, proposal_id, source_update_id, category, priority, state)
            SELECT @tenant, @id, @source, @category, @priority, 1
            WHERE EXISTS (
                SELECT 1 FROM residentchat.telegram_intake
                WHERE tenant_id = @tenant AND update_id = @source AND state = 1)
            ON CONFLICT (tenant_id, source_update_id) DO NOTHING
            RETURNING proposal_id
            """, db, tx);
        cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
        cmd.Parameters.AddWithValue("source", NpgsqlDbType.Bigint, admittedUpdateId);
        cmd.Parameters.AddWithValue("category", NpgsqlDbType.Smallint, (short)category);
        cmd.Parameters.AddWithValue("priority", NpgsqlDbType.Smallint, (short)priority);
        var added = await cmd.ExecuteScalarAsync(ct) is Guid;
        await tx.CommitAsync(ct);
        return added ? id : null;
    }

    public async Task<IReadOnlyList<DurableProposalItem>> ListAsync(
        Guid tenantId, CancellationToken ct)
    {
        ValidateTenant(tenantId);
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await SetTenantAsync(db, tx, tenantId, ct);
        var items = new List<DurableProposalItem>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT proposal_id, category, priority, state, ticket_id
            FROM residentchat.operator_proposals
            WHERE tenant_id = @tenant
            ORDER BY created_at DESC, proposal_id DESC
            LIMIT 100
            """, db, tx))
        {
            cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                items.Add(new DurableProposalItem(
                    CaseId(reader.GetGuid(0)),
                    ((TicketCategory)reader.GetInt16(1)).ToString(),
                    ((TicketPriority)reader.GetInt16(2)).ToString(),
                    reader.GetInt16(3) switch
                    {
                        1 => "Pending", 2 => "Approved", 3 => "Rejected",
                        _ => throw new InvalidOperationException("Unknown proposal state.")
                    },
                    reader.IsDBNull(4) ? null : reader.GetGuid(4)));
        }
        await tx.CommitAsync(ct);
        return items;
    }

    public async Task<DurableProposalChangeResult> ChangeAsync(Guid tenantId,
        string caseId, string? confirmation, bool approve, string actorHash,
        CancellationToken ct)
    {
        ValidateTenant(tenantId);
        if (!TryParseCaseId(caseId, out var proposalId))
            return DurableProposalChangeResult.Missing;
        if (confirmation != (approve ? "APPROVE " : "REJECT ") + caseId)
            return DurableProposalChangeResult.Invalid;
        if (actorHash.Length != 64
            || actorHash.AsSpan().IndexOfAnyExcept("0123456789ABCDEF") >= 0)
            throw new ArgumentException("Invalid actor audit hash.");

        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await SetTenantAsync(db, tx, tenantId, ct);
        short category = 0, priority = 0, state = 0;
        await using (var cmd = new NpgsqlCommand("""
            SELECT category, priority, state FROM residentchat.operator_proposals
            WHERE tenant_id = @tenant AND proposal_id = @id FOR UPDATE
            """, db, tx))
        {
            cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, proposalId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return DurableProposalChangeResult.Missing;
            category = reader.GetInt16(0);
            priority = reader.GetInt16(1);
            state = reader.GetInt16(2);
        }
        if (state != 1) return DurableProposalChangeResult.Conflict;

        Guid? ticketId = approve ? Guid.NewGuid() : null;
        if (approve)
        {
            short status = priority == (short)TicketPriority.Urgent
                ? (short)TicketStatus.Escalated : (short)TicketStatus.Open;
            await using (var create = new NpgsqlCommand("""
                INSERT INTO residentchat.tickets
                    (tenant_id, ticket_id, category, priority, status, version)
                VALUES (@tenant, @ticket, @category, @priority, @status, 1)
                """, db, tx))
            {
                create.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
                create.Parameters.AddWithValue("ticket", NpgsqlDbType.Uuid, ticketId!.Value);
                create.Parameters.AddWithValue("category", NpgsqlDbType.Smallint, category);
                create.Parameters.AddWithValue("priority", NpgsqlDbType.Smallint, priority);
                create.Parameters.AddWithValue("status", NpgsqlDbType.Smallint, status);
                await create.ExecuteNonQueryAsync(ct);
            }
            await using (var audit = new NpgsqlCommand("""
                INSERT INTO residentchat.ticket_audit
                    (tenant_id, ticket_id, action, actor_hash)
                VALUES (@tenant, @ticket, @action, @actor)
                """, db, tx))
            {
                audit.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
                audit.Parameters.AddWithValue("ticket", NpgsqlDbType.Uuid, ticketId!.Value);
                audit.Parameters.AddWithValue("action", NpgsqlDbType.Text,
                    status == (short)TicketStatus.Escalated ? "created_escalated" : "created");
                audit.Parameters.AddWithValue("actor", NpgsqlDbType.Text, actorHash);
                await audit.ExecuteNonQueryAsync(ct);
            }
        }

        await using (var update = new NpgsqlCommand("""
            UPDATE residentchat.operator_proposals
            SET state = @state, ticket_id = @ticket, decided_at = now()
            WHERE tenant_id = @tenant AND proposal_id = @id AND state = 1
            """, db, tx))
        {
            update.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            update.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, proposalId);
            update.Parameters.AddWithValue("state", NpgsqlDbType.Smallint,
                (short)(approve ? 2 : 3));
            update.Parameters.AddWithValue("ticket", NpgsqlDbType.Uuid,
                ticketId.HasValue ? ticketId.Value : DBNull.Value);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Proposal lost its transaction lock.");
        }
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO residentchat.operator_proposal_decisions
                (tenant_id, proposal_id, decision, actor_hash)
            VALUES (@tenant, @id, @decision, @actor)
            """, db, tx))
        {
            audit.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            audit.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, proposalId);
            audit.Parameters.AddWithValue("decision", NpgsqlDbType.Smallint,
                (short)(approve ? 2 : 3));
            audit.Parameters.AddWithValue("actor", NpgsqlDbType.Text, actorHash);
            await audit.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return approve
            ? DurableProposalChangeResult.Approved : DurableProposalChangeResult.Rejected;
    }

    private void ValidateTenant(Guid tenantId)
    {
        if (!options.Ready || tenantId == Guid.Empty)
            throw new InvalidOperationException("Durable proposal test mode is not ready.");
    }

    private static async Task SetTenantAsync(NpgsqlConnection db,
        NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var set = new NpgsqlCommand(
            "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx);
        set.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenantId.ToString("D"));
        await set.ExecuteNonQueryAsync(ct);
    }
}
