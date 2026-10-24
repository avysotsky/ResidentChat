using Npgsql;
using NpgsqlTypes;
using ResidentChat.Api.Operator;

namespace ResidentChat.Api.Operator;

/// <summary>
/// RC-27 permanent-database operator proposal listing, READ ONLY.
/// This store needs only a signed operator JWT / tenant scope, not Telegram
/// ingestion, a model, an HMAC of chat messages, or mutating permissions.
/// </summary>
public sealed class PostgresOperatorQueueReadStore(string connectionString)
{
    public async Task<IReadOnlyList<DurableProposalItem>> ListAsync(
        Guid tenantId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            throw new InvalidOperationException("RC27 tenant required.");

        var items = new List<DurableProposalItem>();
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx))
        {
            scope.Parameters.AddWithValue("tenant", NpgsqlDbType.Text,
                tenantId.ToString("D"));
            await scope.ExecuteNonQueryAsync(ct);
        }

        await using (var query = new NpgsqlCommand("""
            SELECT proposal_id, category, priority, state, ticket_id
            FROM residentchat.operator_proposals
            WHERE tenant_id = @tenant
            ORDER BY created_at DESC, proposal_id DESC LIMIT 100
            """, db, tx))
        {
            query.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                short state = reader.GetInt16(3);
                if (state is < 1 or > 3)
                    throw new InvalidOperationException("RC27 unknown proposal state.");
                items.Add(new DurableProposalItem(
                    PostgresDurableProposalStore.CaseId(reader.GetGuid(0)),
                    ((ResidentChat.Core.Tickets.TicketCategory)reader.GetInt16(1)).ToString(),
                    ((ResidentChat.Core.Tickets.TicketPriority)reader.GetInt16(2)).ToString(),
                    state == 1 ? "Pending" : state == 2 ? "Approved" : "Rejected",
                    reader.IsDBNull(4) ? null : reader.GetGuid(4)));
            }
        }
        await tx.CommitAsync(ct);
        return items;
    }
}
