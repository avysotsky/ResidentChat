using Npgsql;
using NpgsqlTypes;
using ResidentChat.Core.Knowledge;

namespace ResidentChat.Api.Knowledge;

/// <summary>
/// Read-only, tenant-scoped PostgreSQL FAQ retrieval. Do not register an HTTP
/// endpoint or send these answers to Telegram in this development slice.
/// The supplied tenant ID MUST come from verified operator identity.
/// Runtime role must be non-owner, NO BYPASSRLS, and have SELECT only.
/// </summary>
public sealed class PostgresApprovedFaqStore(string connectionString)
{
    public async Task<IReadOnlyList<ApprovedFaqHit>> SearchAsync(
        Guid authorizedTenantId, string? searchText, int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (!ApprovedFaqPolicy.TryCreate(
            authorizedTenantId, searchText, maxResults, out var query))
            return [];

        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(cancellationToken);
        await using var tx = await db.BeginTransactionAsync(cancellationToken);

        // Transaction-local tenant scope; pooled sessions never inherit it.
        // RLS supplies a second guard even if a future SQL filter is flawed.
        await using (var tenantCmd = new NpgsqlCommand(
            "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx))
        {
            tenantCmd.Parameters.AddWithValue(
                "tenant", NpgsqlDbType.Text, query.TenantId.ToString("D"));
            await tenantCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var rows = new List<ApprovedFaqHit>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT tenant_id, faq_id, question, answer, source_ref, approved_at
            FROM residentchat.approved_faq
            WHERE tenant_id = @tenant
              AND approved_at IS NOT NULL
              AND search_vector @@ plainto_tsquery('simple'::regconfig, @search_text)
            ORDER BY ts_rank_cd(
                search_vector, plainto_tsquery('simple'::regconfig, @search_text)
            ) DESC, faq_id
            LIMIT @limit
            """, db, tx))
        {
            // No SQL, table name, condition or limit is model-generated.
            cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, query.TenantId);
            cmd.Parameters.AddWithValue("search_text", NpgsqlDbType.Text, query.Text);
            cmd.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, query.MaxResults);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new ApprovedFaqHit(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    new DateTimeOffset(reader.GetDateTime(5))));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return ApprovedFaqPolicy.ValidateResults(query, rows);
    }
}
