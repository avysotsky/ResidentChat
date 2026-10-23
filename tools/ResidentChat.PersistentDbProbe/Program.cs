using Npgsql;

// RC-24: connection proof to the EXISTING permanent database, READ ONLY.
// Executed only in a one-shot --network none SDK Docker container.
// The separate unprivileged runtime password is supplied by a read-only
// bind-mounted local file; never Git, CLI, environment or application logs.
// No Telegram bot/API, no AI/model, no schema migration and no SQL writes.
try
{
    if (args.Length != 1 || args[0] != "--persistent-readonly")
        throw new InvalidOperationException("RC24 mode not explicitly selected.");

    const string secretFile = "/run/secrets/pg_runtime";
    if (!File.Exists(secretFile))
        throw new InvalidOperationException("RC24 local runtime secret is missing.");

    string password = (await File.ReadAllTextAsync(secretFile)).Trim();
    if (password.Length != 64 || password.Any(c => !Uri.IsHexDigit(c)))
        throw new InvalidOperationException("RC24 local runtime secret has invalid format.");

    var connection = new NpgsqlConnectionStringBuilder
    {
        Host = "/var/run/postgresql",
        Port = 5432,
        Database = "residentchat",
        Username = "residentchat_lab_runtime",
        Password = password,
        Pooling = false,
        SslMode = SslMode.Disable,
        Timeout = 5,
        CommandTimeout = 7,
        ApplicationName = "residentchat-rc24-readonly"
    };

    await using var db = new NpgsqlConnection(connection.ConnectionString);
    await db.OpenAsync();

    static async Task<string> ScalarAsync(NpgsqlConnection db, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db);
        var value = await cmd.ExecuteScalarAsync();
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }
    static void Require(bool condition) {
        if (!condition) throw new InvalidOperationException("RC24 read-only assertion failed.");
    }

    Require(await ScalarAsync(db, "SELECT current_database()") == "residentchat");
    Require(await ScalarAsync(db, "SELECT current_user") == "residentchat_lab_runtime");
    Require(await ScalarAsync(db, "SELECT inet_server_addr() IS NULL") == "True");
    Require(int.Parse(await ScalarAsync(db,
        "SELECT current_setting('server_version_num')"),
        System.Globalization.CultureInfo.InvariantCulture) >= 170000);

    Console.WriteLine("RC24_DOTNET10_UNIX_SOCKET_CONNECTION_PASS");
    Console.WriteLine("RC24_DATABASE=residentchat");
    Console.WriteLine("RC24_ROLE=residentchat_lab_runtime");
    Console.WriteLine("RC24_NETWORK=UNIX_SOCKET_ONLY");

    // Pg catalog gives metadata only; role never accesses admin migration
    // ledger, table-owner powers or tenant data. This is exactly the same
    // nine FORCE ROW LEVEL SECURITY tables created in RC-22 migrations.
    const string secureTables = """
        SELECT COUNT(*) FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'residentchat'
          AND c.relkind = 'r'
          AND c.relrowsecurity AND c.relforcerowsecurity
          AND c.relname IN (
            'tickets','ticket_audit','telegram_intake','telegram_reviews',
            'telegram_review_reads','operator_proposals',
            'operator_proposal_decisions','telegram_poll_cursors',
            'telegram_poll_blockers')
        """;
    Require(await ScalarAsync(db, secureTables) == "9");

    // Do not set a tenant identity here. All guarded SELECTs must return
    // zero despite the database being durable and potentially nonempty.
    foreach (string table in new[] {
        "tickets", "telegram_intake", "telegram_reviews",
        "operator_proposals", "telegram_poll_cursors",
        "telegram_poll_blockers"
    })
    {
        var count = await ScalarAsync(db,
            "SELECT COUNT(*) FROM residentchat." + table);
        Require(count == "0");
    }
    Console.WriteLine("RC24_FORCE_RLS_TABLES=9");
    Console.WriteLine("RC24_DEFAULT_DENY_WITHOUT_TENANT=PASS");

    // Fail-closed proof: the real application role cannot mutate schema,
    // read the administrative bootstrap ledger or gain CREATEDB/owner power.
    const string safeRole = """
        SELECT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls
               AND NOT rolcreatedb AND NOT rolcreaterole
        FROM pg_roles WHERE rolname = current_user
        """;
    Require(await ScalarAsync(db, safeRole) == "True");
    Require(await ScalarAsync(db,
        "SELECT has_table_privilege(current_user, 'residentchat.schema_bootstrap', 'SELECT')") == "False");

    Console.WriteLine("RC24_ADMIN_LEDGER_INACCESSIBLE=YES");
    Console.WriteLine("RC24_POSTGRES_RUNTIME_ROLE_LIMITED=PASS");
    Console.WriteLine("RC24_SQL_WRITES=NONE");
    Console.WriteLine("RC24_TELEGRAM_POLLING=OFF");
    Console.WriteLine("RC24_TELEGRAM_UPDATES_ACKED=NO");
    Console.WriteLine("RC24_PERSISTENT_DOTNET_READONLY_PASS");
    return 0;
}
catch
{
    // Npgsql exceptions can echo connection strings containing credentials.
    // Never log exception messages, stack trace or runtime secret.
    Console.Error.WriteLine("RC24_DOTNET_READONLY_FAIL_SAFE");
    return 1;
}
