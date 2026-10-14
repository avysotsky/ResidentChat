using Npgsql;
using NpgsqlTypes;
using ResidentChat.Api.Knowledge;
using ResidentChat.Core.Knowledge;

// RC-03b: a REAL PostgreSQL gate. Not invoked by the regular six-case Qwen
// evaluation or the network-free ResidentChat.SmokeTests watcher.
// A disposable PostgreSQL cluster and a distinct restricted runtime role
// must be provided by tools/rc-03b-faq-pg-acceptance.sh.
// The only outputs are fixed check labels; never print connection strings,
// credentials, database contents or synthetic record bodies.
const string DbPrefix = "residentchat_faq_acceptance_";
const string RuntimeRole = "residentchat_faq_reader";
var tenantA = Guid.Parse("aa11aa11-1111-4111-8111-111111111111");
var tenantB = Guid.Parse("bb22bb22-2222-4222-8222-222222222222");
var faqA = Guid.Parse("cc33cc33-3333-4333-8333-333333333333");
var faqB = Guid.Parse("dd44dd44-4444-4444-8444-444444444444");

try
{
    var adminString = Environment.GetEnvironmentVariable("RC03B_TEST_PG_ADMIN");
    var runtimeString = Environment.GetEnvironmentVariable("RC03B_TEST_PG_RUNTIME");
    Ensure(!string.IsNullOrWhiteSpace(adminString)
        && !string.IsNullOrWhiteSpace(runtimeString),
        "explicit synthetic-only PostgreSQL configuration required");

    var admin = new NpgsqlConnectionStringBuilder(adminString);
    var runtime = new NpgsqlConnectionStringBuilder(runtimeString);
    // Refuse all production/cloud databases, even when env vars are present.
    // The harness guarantees a short ephemeral Unix-socket directory in /tmp.
    Ensure((admin.Database ?? "").StartsWith(DbPrefix, StringComparison.Ordinal)
        && admin.Database == runtime.Database
        && (admin.Host ?? "").StartsWith("/tmp/rc03bpg.", StringComparison.Ordinal)
        && admin.Host == runtime.Host
        && admin.Port == runtime.Port
        && runtime.Username == RuntimeRole
        && admin.Username != RuntimeRole,
        "dedicated local socket, acceptance database and distinct runtime role required");

    // Install schema/seed with migration owner, NEVER runtime credentials.
    await using (var privileged = new NpgsqlConnection(admin.ConnectionString))
    {
        await privileged.OpenAsync();
        var path = Path.Combine(AppContext.BaseDirectory,
            "migrations", "002_approved_faq.sql");
        var migration = await File.ReadAllTextAsync(path);
        await using (var cmd = new NpgsqlCommand(migration, privileged))
            await cmd.ExecuteNonQueryAsync();

        // This is the only grant for the test role. No update/admin grants.
        await using (var grant = new NpgsqlCommand("""
            GRANT USAGE ON SCHEMA residentchat TO residentchat_faq_reader;
            GRANT SELECT ON residentchat.approved_faq TO residentchat_faq_reader;
            """, privileged))
            await grant.ExecuteNonQueryAsync();

        await using (var seed = new NpgsqlCommand("""
            INSERT INTO residentchat.approved_faq
                (tenant_id, faq_id, question, answer, source_ref, approved_at)
            VALUES
                (@ta, @fa, 'Тест: графік диспетчера А',
                 'Лише синтетична відповідь для будинку А',
                 'synthetic://faq/a', now()),
                (@tb, @fb, 'Тест: графік диспетчера Б',
                 'Лише синтетична відповідь для будинку Б',
                 'synthetic://faq/b', now());
            """, privileged))
        {
            seed.Parameters.AddWithValue("ta", NpgsqlDbType.Uuid, tenantA);
            seed.Parameters.AddWithValue("tb", NpgsqlDbType.Uuid, tenantB);
            seed.Parameters.AddWithValue("fa", NpgsqlDbType.Uuid, faqA);
            seed.Parameters.AddWithValue("fb", NpgsqlDbType.Uuid, faqB);
            await seed.ExecuteNonQueryAsync();
        }

        // The table must physically reject unsigned/unapproved inserts.
        try
        {
            await using var invalid = new NpgsqlCommand("""
                INSERT INTO residentchat.approved_faq
                    (tenant_id, faq_id, question, answer, source_ref, approved_at)
                VALUES (@tenant, @id, 'Тест', 'Тест', 'synthetic://invalid', NULL);
                """, privileged);
            invalid.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantA);
            invalid.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, Guid.NewGuid());
            await invalid.ExecuteNonQueryAsync();
            throw new InvalidOperationException("Unapproved PostgreSQL row accepted");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.NotNullViolation)
        {
            // Required fail-closed outcome.
        }
    }

    await using (var db = new NpgsqlConnection(runtime.ConnectionString))
    {
        await db.OpenAsync();
        await using (var roleCheck = new NpgsqlCommand("""
            SELECT r.rolsuper, r.rolbypassrls, r.rolcreatedb,
                   r.rolcreaterole, c.relrowsecurity, c.relforcerowsecurity,
                   pg_get_userbyid(c.relowner) = current_user,
                   has_table_privilege(current_user, c.oid, 'SELECT'),
                   has_table_privilege(current_user, c.oid, 'INSERT'),
                   has_table_privilege(current_user, c.oid, 'UPDATE'),
                   has_table_privilege(current_user, c.oid, 'DELETE')
            FROM pg_roles r
            JOIN pg_class c ON c.oid = 'residentchat.approved_faq'::regclass
            WHERE r.rolname = current_user;
            """, db))
        await using (var reader = await roleCheck.ExecuteReaderAsync())
        {
            Ensure(await reader.ReadAsync(), "runtime role exists");
            Ensure(!reader.GetBoolean(0) && !reader.GetBoolean(1)
                && !reader.GetBoolean(2) && !reader.GetBoolean(3),
                "runtime role is non-superuser, no bypass RLS, no DDL");
            Ensure(reader.GetBoolean(4) && reader.GetBoolean(5)
                && !reader.GetBoolean(6),
                "FAQ RLS enabled and forced, runtime is not table owner");
            Ensure(reader.GetBoolean(7) && !reader.GetBoolean(8)
                && !reader.GetBoolean(9) && !reader.GetBoolean(10),
                "runtime role has SELECT only");
        }

        Ensure(await CountAsync(db) == 0,
            "RLS denies all FAQ without tenant session scope");

        await using (var transaction = await db.BeginTransactionAsync())
        {
            await SetTenantAsync(db, transaction, tenantA);
            Ensure(await CountAsync(db, transaction) == 1,
                "RLS exposes only tenant A when tenant A is set");
            await transaction.CommitAsync();
        }
        Ensure(await CountAsync(db) == 0,
            "transaction-local tenant setting does not leak after commit");

        await using (var transaction = await db.BeginTransactionAsync())
        {
            await SetTenantAsync(db, transaction, tenantB);
            Ensure(await CountAsync(db, transaction) == 1,
                "RLS exposes only tenant B when tenant B is set");
            await transaction.RollbackAsync();
        }
        Ensure(await CountAsync(db) == 0,
            "transaction-local tenant setting does not leak after rollback");
    }

    var store = new PostgresApprovedFaqStore(runtime.ConnectionString);
    var resultsA = await store.SearchAsync(tenantA, "графік", 2);
    Ensure(resultsA.Count == 1 && resultsA[0].FaqId == faqA
        && resultsA[0].TenantId == tenantA
        && resultsA[0].SourceRef == "synthetic://faq/a",
        "FTS retrieves exactly approved matching tenant A document");

    var resultsB = await store.SearchAsync(tenantB, "графік", 2);
    Ensure(resultsB.Count == 1 && resultsB[0].FaqId == faqB
        && resultsB[0].TenantId == tenantB
        && resultsB[0].SourceRef == "synthetic://faq/b",
        "same FTS query cannot retrieve tenant A from tenant B");

    Ensure((await store.SearchAsync(tenantA, "неіснуючасинтетичнафраза", 2)).Count == 0,
        "no lexical match returns empty, not a fabricated answer");
    Ensure((await store.SearchAsync(Guid.Empty, "графік", 2)).Count == 0,
        "empty authorized tenant denied before database request");
    Ensure((await store.SearchAsync(tenantA, "графік", 4)).Count == 0,
        "unbounded result request denied");
    Ensure((await store.SearchAsync(tenantA,
        "графік'); DROP TABLE residentchat.approved_faq; --", 2)).Count <= 1,
        "SQL-like text treated as lexical parameter, not executable SQL");

    // Read-only RLS must survive connection reuse with pooling enabled.
    NpgsqlConnection.ClearAllPools();
    await using (var reused = new NpgsqlConnection(runtime.ConnectionString))
    {
        await reused.OpenAsync();
        Ensure(await CountAsync(reused) == 0,
            "pooled runtime connection starts without a previous tenant scope");

        try
        {
            await using var forbidden = new NpgsqlCommand("""
                UPDATE residentchat.approved_faq
                SET answer = 'forbidden' WHERE tenant_id = @tenant
                """, reused);
            forbidden.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantA);
            await forbidden.ExecuteNonQueryAsync();
            throw new InvalidOperationException("runtime FAQ write was permitted");
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            // Required fail-closed outcome.
        }
    }

    // Synthetic test does not leave any resident data; ephemeral cluster
    // is destroyed by the shell harness even on test failure.
    Console.WriteLine("RC-03B FAQ PG ACCEPTANCE PASS: migration, runtime role, forced RLS, " +
        "two tenants, tenant reset, lexical FTS, write denial, synthetic cleanup.");
    return 0;
}
catch (Exception ex)
{
    // No PII, credentials, connection strings or SQL text are emitted.
    Console.Error.WriteLine("RC-03B FAQ PG ACCEPTANCE FAIL: " + ex.GetType().Name);
    return 1;
}

static async Task<long> CountAsync(NpgsqlConnection db, NpgsqlTransaction? tx = null)
{
    await using var cmd = new NpgsqlCommand(
        "SELECT count(*) FROM residentchat.approved_faq", db, tx);
    return (long)(await cmd.ExecuteScalarAsync())!;
}

static async Task SetTenantAsync(
    NpgsqlConnection db, NpgsqlTransaction tx, Guid tenant)
{
    await using var cmd = new NpgsqlCommand(
        "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx);
    cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, tenant.ToString("D"));
    await cmd.ExecuteNonQueryAsync();
}

static void Ensure(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
