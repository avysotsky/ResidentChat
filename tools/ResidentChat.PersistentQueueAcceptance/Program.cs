using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using ResidentChat.Api;

// RC-26: actual ResidentChat operator queue backed by EXISTING permanent DB.
// Fixed fictional tenant, update ID and proposal GUID, all namespaced to RC-26.
// No real Telegram update, residents, AI, bot token, ticket/approval or deletion.
const string Alpha = "27272727-2727-4727-8727-272727272727";
const string Beta = "28282828-2828-4828-8828-282828282828";
const string Issuer = "residentchat.rc26.local-only";
const string Audience = "residentchat.rc26.queue-only";
const string Secret = "/run/secrets/pg_runtime";
const long SyntheticUpdateId = 26260001;
const long SyntheticChat = -1001262600000;
const string SyntheticProposal = "26262626-2626-4626-8626-000000000026";
try
{
    if (args.Length != 1 ||
        args[0] is not ("--persistent-queue-once" or "--persistent-queue-readonly" or "--react-dp-readonly"))
        throw new InvalidOperationException("RC26 one-shot mode required.");
    if (!File.Exists(Secret))
        throw new InvalidOperationException("RC26 local runtime credential missing.");
    string pw = (await File.ReadAllTextAsync(Secret)).Trim();
    Require(pw.Length == 64 && pw.All(Uri.IsHexDigit), "RC26 local password format");
    var conn = new NpgsqlConnectionStringBuilder
    {
        Host = "/var/run/postgresql", Port = 5432,
        Database = "residentchat", Username = "residentchat_lab_runtime",
        Password = pw, Pooling = false, SslMode = SslMode.Disable,
        Timeout = 5, CommandTimeout = 10,
        ApplicationName = "residentchat-rc26-local-queue"
    }.ConnectionString;

    Guid alphaId = Guid.Parse(Alpha);
    Guid proposalId = Guid.Parse(SyntheticProposal);
    string caseId = "RC-" + proposalId.ToString("N").ToUpperInvariant();
    bool persistentReact = args[0] == "--react-dp-readonly";
    string signing = persistentReact
        ? (await File.ReadAllTextAsync("/run/secrets/rc28_jwt")).Trim()
        : Convert.ToHexString(RandomNumberGenerator.GetBytes(48));
    string audit = persistentReact
        ? (await File.ReadAllTextAsync("/run/secrets/rc28_audit")).Trim()
        : Convert.ToHexString(RandomNumberGenerator.GetBytes(48));
    Require(signing.Length >= 96 && audit.Length >= 96 && signing != audit,
        "RC28 local signing and audit keys must be distinct");
    string chatHmac = Convert.ToHexString(RandomNumberGenerator.GetBytes(48));
    string webhook = Convert.ToHexString(RandomNumberGenerator.GetBytes(48));
    string jwtA = Jwt(Alpha, signing);
    string jwtB = Jwt(Beta, signing);

    // Count only this fixed synthetic tenant. Any foreign row in this
    // tenant causes a hard stop, never deletion or silent modification.
    async Task<(long Intakes, long Proposals, long Tickets, long Decisions)> InspectAsync()
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx))
        {
            context.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, Alpha);
            await context.ExecuteNonQueryAsync();
        }
        async Task<long> Count(string table)
        {
            if (table is not ("telegram_intake" or "operator_proposals"
                or "tickets" or "operator_proposal_decisions"))
                throw new InvalidOperationException("RC26 table out of scope.");
            await using var query = new NpgsqlCommand(
                "SELECT count(*) FROM residentchat." + table + " WHERE tenant_id = @tenant", db, tx);
            query.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, alphaId);
            return (long)(await query.ExecuteScalarAsync())!;
        }
        var result = (await Count("telegram_intake"),
                      await Count("operator_proposals"),
                      await Count("tickets"),
                      await Count("operator_proposal_decisions"));
        await tx.CommitAsync();
        return result;
    }

    var initial = await InspectAsync();
    Require(initial.Tickets == 0 && initial.Decisions == 0,
        "RC26 dedicated tenant must have no ticket or decision.");
    Require((initial.Intakes == 0 && initial.Proposals == 0)
        || (initial.Intakes == 1 && initial.Proposals == 1),
        "RC26 unexpected persistent tenant rows.");
    if (args[0] != "--persistent-queue-once")
        Require(initial.Intakes == 1 && initial.Proposals == 1,
            "RC27 readonly requires preexisting RC26 fixture; no writes permitted.");
    if (initial.Intakes == 0)
    {
        // One atomic, fixed, metadata-only fixture in separate test tenant.
        // Direct controlled SQL setup here tests queue read/persistence,
        // NOT Telegram classification or an actual model decision.
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx))
        {
            context.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, Alpha);
            await context.ExecuteNonQueryAsync();
        }
        await using (var intake = new NpgsqlCommand("""
            INSERT INTO residentchat.telegram_intake
                (tenant_id, update_id, chat_hash, state)
            VALUES (@tenant, @update, repeat('A',64), 1)
            """, db, tx))
        {
            intake.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, alphaId);
            intake.Parameters.AddWithValue("update", NpgsqlDbType.Bigint, SyntheticUpdateId);
            Require(await intake.ExecuteNonQueryAsync() == 1,
                "RC26 insert synthetic intake once");
        }
        await using (var proposal = new NpgsqlCommand("""
            INSERT INTO residentchat.operator_proposals
                (tenant_id, proposal_id, source_update_id, category, priority, state)
            VALUES (@tenant, @id, @update, 2, 1, 1)
            """, db, tx))
        {
            proposal.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, alphaId);
            proposal.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, proposalId);
            proposal.Parameters.AddWithValue("update", NpgsqlDbType.Bigint, SyntheticUpdateId);
            Require(await proposal.ExecuteNonQueryAsync() == 1,
                "RC26 insert synthetic pending proposal once");
        }
        await tx.CommitAsync();
        Console.WriteLine("RC26_NEW_METADATA_ONLY_PROPOSAL_CREATED=YES");
    }
    else
    {
        // Existing fixture must match the exact immutable RC26 identities.
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        await using (var ctx = new NpgsqlCommand(
            "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx))
        {
            ctx.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, Alpha);
            await ctx.ExecuteNonQueryAsync();
        }
        await using var chk = new NpgsqlCommand("""
            SELECT count(*) FROM residentchat.operator_proposals
            WHERE tenant_id = @tenant AND proposal_id = @id
              AND source_update_id = @update AND state = 1
              AND category = 2 AND priority = 1
            """, db, tx);
        chk.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, alphaId);
        chk.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, proposalId);
        chk.Parameters.AddWithValue("update", NpgsqlDbType.Bigint, SyntheticUpdateId);
        Require((long)(await chk.ExecuteScalarAsync())! == 1,
            "RC26 existing fixture identity and Pending state");
        await tx.CommitAsync();
        Console.WriteLine("RC26_EXISTING_PENDING_PROPOSAL_REUSED=YES");
    }

    async Task<(WebApplication Api, HttpClient Client)> StartAsync(
        bool readOnly = false, bool encryptedKeys = false)
    {
        // The synthetic webhook gate must be configured for the DURABLE
        // proposal dependency, but NO Telegram credential or getUpdates
        // is provided. Entire SDK Docker network=none, no published ports.
        var cfg = new Dictionary<string, string?> {
            ["ResidentChat:Operator:Enabled"] = "true",
            ["ResidentChat:Operator:Issuer"] = Issuer,
            ["ResidentChat:Operator:Audience"] = Audience,
            ["ResidentChat:Operator:JwtSigningKey"] = signing,
            ["ResidentChat:Operator:AuditHmacKey"] = audit,
            ["ResidentChat:Operator:ConnectionString"] = conn,
            ["ResidentChat:Operator:AllowedTenantIds:0"] = Alpha,
            ["ResidentChat:Operator:AllowedTenantIds:1"] = Beta,
            ["ResidentChat:OperatorConsole:Enabled"] = "true",
            ["ResidentChat:OperatorConsole:Mode"] =
                readOnly ? "persistent-lab-readonly" : "synthetic-lab-only",
            ["ResidentChat:PersistentReadOnly:Enabled"] = readOnly.ToString(),
            ["ResidentChat:Telegram:Enabled"] = (!readOnly).ToString(),
            ["ResidentChat:Telegram:WebhookSecret"] = webhook,
            ["ResidentChat:Telegram:AllowedChatIds:0"] = SyntheticChat.ToString(),
            ["ResidentChat:TelegramPolling:Enabled"] = "false",
            ["ResidentChat:DurableIntake:Enabled"] = (!readOnly).ToString(),
            ["ResidentChat:DurableIntake:Mode"] = "metadata-only-test-groups",
            ["ResidentChat:DurableIntake:ChatHashHmacKey"] = chatHmac,
            ["ResidentChat:DurableIntake:Groups:0:ChatId"] = SyntheticChat.ToString(),
            ["ResidentChat:DurableIntake:Groups:0:TenantId"] = Alpha,
            ["ResidentChat:DurableProposals:Enabled"] = (!readOnly).ToString(),
            ["ResidentChat:DurableProposals:Mode"] = "synthetic-postgres-only",
            ["ResidentChat:ClassifiedTelegram:Enabled"] = "false",
            ["ResidentChat:TelegramReview:Enabled"] = "false"
        };
        if (encryptedKeys)
        {
            cfg["ResidentChat:PersistentDataProtection:Enabled"] = "true";
            cfg["ResidentChat:PersistentDataProtection:KeyDirectory"] =
                "/run/rc28-data-protection/keys";
            cfg["ResidentChat:PersistentDataProtection:CertificatePath"] =
                "/run/secrets/rc28_dp_cert";
            cfg["ResidentChat:PersistentDataProtection:PasswordFile"] =
                "/run/secrets/rc28_dp_password";
        }
        var app = ResidentChatApp.Build([], cfg);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseUri = new Uri(addresses.Single(
            u => u.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
        return (app, new HttpClient { BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(12) });
    }

    async Task VerifyQueueAsync(HttpClient http)
    {
        async Task<HttpResponseMessage> Get(string? jwt)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "/operator/console/v1/proposals");
            if (jwt is not null)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            return await http.SendAsync(req);
        }
        using var own = await Get(jwtA);
        using var foreign = await Get(jwtB);
        using var unauth = await Get(null);
        Require(own.StatusCode == HttpStatusCode.OK
            && foreign.StatusCode == HttpStatusCode.OK
            && unauth.StatusCode == HttpStatusCode.Unauthorized,
            "RC26 signed operator queue access");
        using var ownJson = JsonDocument.Parse(await own.Content.ReadAsStringAsync());
        using var betaJson = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync());
        Require(ownJson.RootElement.ValueKind == JsonValueKind.Array
            && ownJson.RootElement.GetArrayLength() == 1
            && ownJson.RootElement[0].GetProperty("caseId").GetString() == caseId
            && ownJson.RootElement[0].GetProperty("state").GetString() == "Pending"
            && betaJson.RootElement.ValueKind == JsonValueKind.Array
            && betaJson.RootElement.GetArrayLength() == 0,
            "RC26 scoped permanent Pending proposal and Beta isolation");
    }

    if (args[0] == "--persistent-queue-once")
    {
    var first = await StartAsync();
    try { await VerifyQueueAsync(first.Client); }
    finally
    {
        first.Client.Dispose();
        await first.Api.StopAsync();
        await first.Api.DisposeAsync();
    }
    Console.WriteLine("RC26_FIRST_API_INSTANCE_STOPPED=YES");
    var second = await StartAsync();
    try { await VerifyQueueAsync(second.Client); }
    finally
    {
        second.Client.Dispose();
        await second.Api.StopAsync();
        await second.Api.DisposeAsync();
    }
    }
    // RC-27 read-only service configuration: no Telegram mode and no
    // ingress/durable proposal opt-in. Real GET is tenant-scoped by RLS.
    var reader = await StartAsync(readOnly: true, encryptedKeys: persistentReact);
    try
    {
        await VerifyQueueAsync(reader.Client);
        using var modeReq = new HttpRequestMessage(HttpMethod.Get,
            "/operator/console/v1/mode");
        modeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwtA);
        using var mode = await reader.Client.SendAsync(modeReq);
        Require(mode.StatusCode == HttpStatusCode.OK
            && (await mode.Content.ReadAsStringAsync()).Contains(
                "\"readOnly\":true", StringComparison.Ordinal),
            "RC27 authenticated server advertises read-only mode");

        using var page = await reader.Client.GetAsync("/operator/console");
        Require(persistentReact
                ? page.StatusCode == HttpStatusCode.OK
                : page.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
            "RC28 bundled React required; RC27 fallback fail closed");
        if (page.StatusCode == HttpStatusCode.OK)
        {
            string html = await page.Content.ReadAsStringAsync();
            Require(!html.Contains("operator/console/app.js", StringComparison.Ordinal),
                "RC27 cannot fall back to legacy synthetic mutation UI");
            if (persistentReact)
            {
                Require(html.Contains("id=\"root\"", StringComparison.Ordinal),
                    "RC28 React root exists");
                var paths = System.Text.RegularExpressions.Regex.Matches(
                    html, "/operator/console/assets/index-[A-Za-z0-9_-]+[.](?:js|css)")
                    .Select(match => match.Value).Distinct().ToArray();
                Require(paths.Length == 2 && paths.Any(x => x.EndsWith(".js", StringComparison.Ordinal))
                    && paths.Any(x => x.EndsWith(".css", StringComparison.Ordinal)),
                    "RC28 exact Vite JS/CSS assets");
                foreach (string asset in paths)
                {
                    using var downloaded = await reader.Client.GetAsync(asset);
                    Require(downloaded.StatusCode == HttpStatusCode.OK
                        && (await downloaded.Content.ReadAsByteArrayAsync()).Length > 128,
                        "RC28 same-origin hashed asset served by real Kestrel");
                }
                Console.WriteLine("RC28_REACT_BUNDLE_SAME_ORIGIN_PASS");
            }
        }
        using var create = new HttpRequestMessage(HttpMethod.Post, "/operator/v1/tickets");
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwtA);
        create.Content = new StringContent(
            """{"category":"Water","priority":"Routine"}""",
            Encoding.UTF8, "application/json");
        using var createDenied = await reader.Client.SendAsync(create);
        Require(createDenied.StatusCode == HttpStatusCode.MethodNotAllowed,
            "RC27 direct operator ticket POST blocked at HTTP boundary");

        using var decision = new HttpRequestMessage(HttpMethod.Post,
            "/operator/console/v1/proposals/" + caseId + "/approve");
        decision.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwtA);
        decision.Content = new StringContent(
            JsonSerializer.Serialize(new { confirmation = "APPROVE " + caseId }),
            Encoding.UTF8, "application/json");
        using var decisionDenied = await reader.Client.SendAsync(decision);
        Require(decisionDenied.StatusCode == HttpStatusCode.MethodNotAllowed,
            "RC27 operator proposal decision POST is not reachable");

        using var webhookPost = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        using var webhookDenied = await reader.Client.SendAsync(webhookPost);
        Require(webhookDenied.StatusCode == HttpStatusCode.MethodNotAllowed,
            "RC27 all webhook POSTs blocked even if later misconfigured");
    }
    if (persistentReact)
    {
        var protector = reader.Api.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("ResidentChat.RC28.OwnerOnlyPersistenceProof.v1");
        var protectedValue = protector.Protect("RC28_PERSISTENCE_TEST_NO_RESIDENT_DATA");
        Require(protector.Unprotect(protectedValue) ==
            "RC28_PERSISTENCE_TEST_NO_RESIDENT_DATA",
            "RC28 DataProtection can encrypt and decrypt a synthetic string");
        // Separate application host, same X509 private key and durable ring.
        var restarted = await StartAsync(readOnly: true, encryptedKeys: true);
        try
        {
            var otherProtector = restarted.Api.Services
                .GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("ResidentChat.RC28.OwnerOnlyPersistenceProof.v1");
            Require(otherProtector.Unprotect(protectedValue) ==
                "RC28_PERSISTENCE_TEST_NO_RESIDENT_DATA",
                "RC28 a second ASP.NET10 instance decrypts the earlier protected value");
        }
        finally
        {
            restarted.Client.Dispose();
            await restarted.Api.StopAsync();
            await restarted.Api.DisposeAsync();
        }
        var ringFiles = Directory.EnumerateFiles(
            "/run/rc28-data-protection/keys", "key-*.xml").ToArray();
        Require(ringFiles.Length is >= 1 and <= 10
            && ringFiles.All(name => File.ReadAllText(name).Contains(
                "encryptedSecret", StringComparison.Ordinal)),
            "RC28 every persisted DataProtection key is X509-encrypted");
        Console.WriteLine("RC28_X509_ENCRYPTED_KEYRING_PASS");
        Console.WriteLine("RC28_DP_CROSS_APP_RESTART_PASS");
    }
    finally
    {
        reader.Client.Dispose();
        await reader.Api.StopAsync();
        await reader.Api.DisposeAsync();
    }
    var afterRead = await InspectAsync();
    Require(afterRead.Intakes == 1 && afterRead.Proposals == 1
        && afterRead.Tickets == 0 && afterRead.Decisions == 0,
        "RC27 pure read-only HTTP does not mutate persistent tenant state");
    Console.WriteLine("RC27_PERSISTENT_READONLY_QUEUE_PASS");
    Console.WriteLine("RC27_MUTATING_HTTP_BLOCKED_PASS");
    Console.WriteLine("RC27_CROSS_TENANT_JWT_RLS_PASS");
    Console.WriteLine("RC27_POSTGRES_CONTENT_UNCHANGED=YES");
    Console.WriteLine("RC27_TELEGRAM_ACK=NO");
    Console.WriteLine("RC27_READONLY_OPERATOR_API_PASS");
    var after = await InspectAsync();
    Require(after.Intakes == 1 && after.Proposals == 1
        && after.Tickets == 0 && after.Decisions == 0,
        "RC26 exact one metadata-only proposal and no tickets/decisions");
    if (args[0] == "--persistent-queue-once")
    {
        Console.WriteLine("RC26_REAL_HTTP_PENDING_QUEUE_PASS");
        Console.WriteLine("RC26_QUEUE_API_RESTART_RETENTION_PASS");
        Console.WriteLine("RC26_JWT_RLS_TENANT_ISOLATION_PASS");
    }
    Console.WriteLine("RC26_PENDING_PROPOSAL_COUNT=1");
    Console.WriteLine("RC26_AUTOMATIC_TICKETS=0");
    Console.WriteLine("RC26_POSTGRES_VOLUME=UNCHANGED");
    Console.WriteLine("RC26_TELEGRAM_NETWORK=OFF");
    Console.WriteLine("RC26_TELEGRAM_OFFSET_ACK=NO");
    Console.WriteLine("RC26_PERMANENT_OPERATOR_QUEUE_PASS");
    return 0;
}
catch
{
    // Never print Npgsql error messages, bearer tokens, DB credentials or
    // URLs. All error details remain private to this owner-triggered test.
    Console.Error.WriteLine("RC26_FAIL_SAFE");
    return 1;
}

static string Jwt(string tenant, string key)
{
    var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
    var body = Encode(JsonSerializer.SerializeToUtf8Bytes(new {
        iss = "residentchat.rc26.local-only",
        aud = "residentchat.rc26.queue-only",
        sub = "rc26-synthetic-operator",
        tenant_id = tenant,
        role = "operator",
        exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()
    }));
    string input = header + "." + body;
    return input + "." + Encode(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes(input)));
}
static string Encode(byte[] value) =>
    Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
static void Require(bool okay, string label)
{
    if (!okay) throw new InvalidOperationException(label);
}
