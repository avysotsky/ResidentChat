using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ResidentChat.Api;
using ResidentChat.Core.Tickets;
using ResidentChat.Api.Operator;
using ResidentChat.Core.Demo;
using ResidentChat.Core.LocalAi;

const string Issuer = "residentchat.synthetic.test";
const string Audience = "residentchat.synthetic.operator";
const string SigningKey = "SYNTHETIC_SIGNING_KEY_ONLY_1234567890_ABCDEFGHIJ";
const string AuditKey = "SYNTHETIC_AUDIT_KEY_ONLY_1234567890_QWERTYZXCV";
const string Tenant1 = "11111111-1111-4111-8111-111111111111";
const string Tenant2 = "22222222-2222-4222-8222-222222222222";
const string Outsider = "33333333-3333-4333-8333-333333333333";
const string SyntheticTelegramSecret = "RC10_WEBHOOK_SYNTHETIC_KEY_0123456789_ABC";
const string SyntheticHmacKey = "RC10_DISTINCT_CHAT_HMAC_KEY_0123456789_XYZ";
const long AlphaChat = -1001234567890;
const long BetaChat = -1009876543210;

try
{
    var adminConn = Environment.GetEnvironmentVariable("RC02_TEST_PG_ADMIN");
    var appConn = Environment.GetEnvironmentVariable("RC02_TEST_PG_RUNTIME");
    Check(!string.IsNullOrWhiteSpace(adminConn) && !string.IsNullOrWhiteSpace(appConn),
        "synthetic Postgres connection environment required");
    if (Environment.GetEnvironmentVariable("RC05_TEST_ISOLATED") == "1")
    {
        var admin = new NpgsqlConnectionStringBuilder(adminConn);
        var runtime = new NpgsqlConnectionStringBuilder(appConn);
        Check((admin.Host ?? "").StartsWith("/tmp/rc05pg.", StringComparison.Ordinal)
            && admin.Host == runtime.Host
            && (admin.Database ?? "").StartsWith("residentchat_approval_lab_", StringComparison.Ordinal)
            && admin.Database == runtime.Database
            && admin.Port == runtime.Port
            && admin.Username == "postgres"
            && runtime.Username == "residentchat_synthetic_app",
            "approval lab requires isolated Unix socket and synthetic database");
    }
    await SetupDatabaseAsync(adminConn!);

    // Explicit owner-triggered laboratory operation. Unlike automated
    // integration acceptance, this path requires a HUMAN to type the exact
    // case confirmation in the launch command. Still synthetic credentials.
    if (args.Length > 0)
    {
        Check(Environment.GetEnvironmentVariable("RC05_TEST_ISOLATED") == "1",
            "manual demonstration must use ephemeral lab only");
        Check(args.Length == 4 && args[0] == "--approve"
            && args[1] == "ALPHA-WATER" && args[2] == "--confirm",
            "only opt-in synthetic ALPHA-WATER approval is supported");
        var tenant = Guid.Parse(Tenant1);
        var manualPlan = DemoDispatchPolicy.Plan(tenant,
            new ResidentDecision(true, ResidentIntent.Maintenance, "water",
                ResidentUrgency.Urgent, true));
        Console.WriteLine("RC-05 OPERATOR PREVIEW: ALPHA-WATER | Water | Urgent | human approval");
        Check(DemoApprovalPolicy.TryApprove(tenant, args[1],
            manualPlan, args[3], out var manualApproved) && manualApproved is not null,
            "operator must explicitly confirm exact approval text");
        var manualConfirmed = manualApproved ?? throw new InvalidOperationException("No approved synthetic command");
        await CheckTotalTicketsAsync(adminConn!, 0,
            "manual demonstration has no ticket before approval");
        await using var demonstration = await StartAsync(appConn!, true, true);
        var approvedRequest = JsonSerializer.Serialize(new {
            category = manualConfirmed.Category.ToString(),
            priority = manualConfirmed.Priority.ToString()
        });
        using var response = await Post(demonstration.Client,
            "/operator/v1/tickets", approvedRequest,
            Jwt(Tenant1, "operator", "synthetic-human-demo"));
        Check(response.StatusCode == HttpStatusCode.Created,
            "approved synthetic operator POST returns HTTP 201");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = payload.RootElement.GetProperty("ticketId").GetGuid();
        Check(payload.RootElement.GetProperty("tenantId").GetGuid() == tenant
            && payload.RootElement.GetProperty("status").GetInt32()
                == (int)TicketStatus.Escalated,
            "signed identity and urgent escalation preserved");
        await CheckTotalTicketsAsync(adminConn!, 1,
            "manual demonstration wrote exactly one synthetic ticket");
        using var verify = await Get(demonstration.Client,
            "/operator/v1/tickets/" + id, Jwt(Tenant1, "operator", "synthetic-human-demo"));
        Check(verify.StatusCode == HttpStatusCode.OK,
            "approved ticket is readable via authorized operator API");
        Console.WriteLine("RC-05 MANUAL LAB PASS: one explicitly approved synthetic ticket persisted");
        Console.WriteLine("RC-05 LAB CLEANUP: disposable database will be deleted by launcher");
        return 0;
    }

    await using (var disabled = await StartAsync(appConn!, false, true))
    {
        var response = await Post(disabled.Client, "/operator/v1/tickets",
            """{"category":"Heating","priority":"Routine"}""", null);
        Check(response.StatusCode == HttpStatusCode.NotFound, "API disabled by default");
    }

    await using (var unconfigured = await StartAsync(appConn!, true, false))
    {
        var response = await Post(unconfigured.Client, "/operator/v1/tickets",
            """{"category":"Heating","priority":"Routine"}""", null);
        Check(response.StatusCode == HttpStatusCode.NotFound, "incomplete config denied");
    }

    await using var server = await StartAsync(appConn!, true, true);
    var client = server.Client;
    var operator1 = Jwt(Tenant1, "operator", "synthetic-user-1");
    var supervisor1 = Jwt(Tenant1, "supervisor", "synthetic-supervisor-1");
    var operator2 = Jwt(Tenant2, "operator", "synthetic-user-2");
    var outsider = Jwt(Outsider, "operator", "synthetic-outsider");

    Check((await Post(client, "/operator/v1/tickets",
        """{"category":"Water","priority":"Urgent"}""", null)).StatusCode
        == HttpStatusCode.Unauthorized, "missing JWT rejected");
    Check((await Post(client, "/operator/v1/tickets",
        """{"category":"Water","priority":"Urgent"}""", operator1 + "tampered")).StatusCode
        == HttpStatusCode.Unauthorized, "modified JWT rejected");
    Check((await Post(client, "/operator/v1/tickets",
        """{"category":"Water","priority":"Urgent"}""", outsider)).StatusCode
        == HttpStatusCode.Forbidden, "unconfigured tenant claim rejected");

    Check((await Post(client, "/operator/v1/tickets",
        """{"category":"Water","priority":"Urgent","message":"synthetic forbidden raw chat"}""", operator1)).StatusCode
        == HttpStatusCode.BadRequest, "text and unexpected fields rejected");
    Check((await Post(client, "/operator/v1/tickets",
        """{"category":"SomethingElse","priority":"Routine"}""", operator1)).StatusCode
        == HttpStatusCode.BadRequest, "unknown category rejected");

    // RC-05: simulate a SPECIFIC human approval in an isolated synthetic-only
    // acceptance. No HTTP ticket POST occurs before the explicit C# gate passes.
    // This test cannot establish real human identity or production approval UX.
    var plan = DemoDispatchPolicy.Plan(Guid.Parse(Tenant1),
        new ResidentDecision(true, ResidentIntent.Maintenance,
            "water", ResidentUrgency.Urgent, true));
    Check(!DemoApprovalPolicy.TryApprove(Guid.Parse(Tenant1), "ALPHA-WATER",
        plan, null, out _), "unapproved proposal must not be written");
    Check(!DemoApprovalPolicy.TryApprove(Guid.Parse(Tenant2), "ALPHA-WATER",
        plan, "APPROVE ALPHA-WATER", out _), "different-tenant approval denied");
    await CheckTotalTicketsAsync(adminConn!, 0, "before any approved HTTP POST");

    Check(DemoApprovalPolicy.TryApprove(Guid.Parse(Tenant1), "ALPHA-WATER",
        plan, "APPROVE ALPHA-WATER", out var approved) && approved is not null,
        "synthetic operator confirmation creates exact metadata command");
    var confirmed = approved ?? throw new InvalidOperationException("No confirmed synthetic command");
    var approvedJson = JsonSerializer.Serialize(new
    {
        category = confirmed.Category.ToString(),
        priority = confirmed.Priority.ToString()
    });

    var created = await Post(client, "/operator/v1/tickets",
        approvedJson, operator1);
    Check(created.StatusCode == HttpStatusCode.Created, "synthetic urgent ticket created");
    using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
    var createdRoot = createdJson.RootElement;
    Guid ticketId = createdRoot.GetProperty("ticketId").GetGuid();
    Check(createdRoot.GetProperty("status").GetInt32() == (int)TicketStatus.Escalated,
        "urgent ticket automatically requires human escalation");
    Check(createdRoot.GetProperty("version").GetInt32() == 1, "initial version 1");
    Check(createdRoot.GetProperty("tenantId").GetGuid() == Guid.Parse(Tenant1)
        && createdRoot.GetProperty("category").GetInt32() == (int)TicketCategory.Water,
        "approved proposal persisted under signed operator tenant");
    await CheckTotalTicketsAsync(adminConn!, 1, "exactly one ticket after confirmed HTTP POST");

    var own = await Get(client, "/operator/v1/tickets/" + ticketId, operator1);
    Check(own.StatusCode == HttpStatusCode.OK, "own tenant reads ticket");
    var foreign = await Get(client, "/operator/v1/tickets/" + ticketId, operator2);
    Check(foreign.StatusCode == HttpStatusCode.NotFound, "cross-tenant read concealed");

    var unauthorizedTransition = await Post(client,
        "/operator/v1/tickets/" + ticketId + "/resolve",
        """{"expectedVersion":1}""", operator1);
    Check(unauthorizedTransition.StatusCode == HttpStatusCode.Forbidden,
        "ordinary operator cannot resolve incidents");

    var crossTenant = await Post(client,
        "/operator/v1/tickets/" + ticketId + "/escalate",
        """{"expectedVersion":1}""", operator2);
    Check(crossTenant.StatusCode == HttpStatusCode.NotFound,
        "cross-tenant mutation concealed");

    var resolved = await Post(client,
        "/operator/v1/tickets/" + ticketId + "/resolve",
        """{"expectedVersion":1}""", supervisor1);
    Check(resolved.StatusCode == HttpStatusCode.OK, "supervisor resolves incident");
    using var resolvedJson = JsonDocument.Parse(await resolved.Content.ReadAsStringAsync());
    Check(resolvedJson.RootElement.GetProperty("version").GetInt32() == 2,
        "optimistic concurrency version increments");

    var stale = await Post(client,
        "/operator/v1/tickets/" + ticketId + "/resolve",
        """{"expectedVersion":1}""", supervisor1);
    Check(stale.StatusCode == HttpStatusCode.Conflict,
        "stale state transition returns conflict");

    var tenant2Ticket = await Post(client, "/operator/v1/tickets",
        """{"category":"Heating","priority":"Routine"}""", operator2);
    Check(tenant2Ticket.StatusCode == HttpStatusCode.Created,
        "second tenant can create independently");

    // RC-06 — browser-backed, loopback-only synthetic operator console.
    // User actions are represented by explicit HTTP POSTs using signed JWTs.
    // No model/Telegram integration, real users, or raw chat content.
    Check((await Get(client, "/operator/console", operator1)).StatusCode
        == HttpStatusCode.NotFound, "synthetic console disabled by default");

    await using (var consoleServer = await StartAsync(appConn!, true, true, true))
    {
        var uiClient = consoleServer.Client;
        using var html = await Get(uiClient, "/operator/console", operator1);
        Check(html.StatusCode == HttpStatusCode.OK &&
            html.Headers.TryGetValues("Content-Security-Policy", out var csp) &&
            csp.Single().Contains("script-src 'self'", StringComparison.Ordinal) &&
            (await html.Content.ReadAsStringAsync()).Contains("Панель диспетчера",
                StringComparison.Ordinal),
            "opt-in synthetic browser UI has strict same-origin CSP");
        Check((await Get(uiClient, "/operator/console/app.js", operator1))
            .StatusCode == HttpStatusCode.OK,
            "static browser script served from same loopback origin");
        using var missingReactAsset = await Get(uiClient,
            "/operator/console/assets/index-NOT-A-REAL-HASH.js", operator1);
        Check(missingReactAsset.StatusCode == HttpStatusCode.NotFound,
            "unknown JS bundle cannot fall through to arbitrary local files");
        using var invalidAsset = await Get(uiClient,
            "/operator/console/assets/config.json", operator1);
        Check(invalidAsset.StatusCode == HttpStatusCode.NotFound,
            "only versioned JS/CSS assets can be requested");
        Check(html.Headers.TryGetValues("Cache-Control", out var noStore)
            && noStore.Any(value => value.Contains("no-store", StringComparison.Ordinal)),
            "synthetic operator HTML explicitly disables browser cache");
 
        // Isolated synthetic asset fixture: check that Vite's same-origin
        // hashed JS/CSS routes actually serve their bytes under strict CSP.
        // Unlike the built React acceptance gate, this requires NO npm cache.
        var bundledRoot = Path.Combine(AppContext.BaseDirectory, "operator-ui");
        var bundledIndex = Path.Combine(bundledRoot, "index.html");
        var bundledAssets = Path.Combine(bundledRoot, "assets");
        var syntheticJs = Path.Combine(bundledAssets, "index-TESTFIXTURE.js");
        var syntheticCss = Path.Combine(bundledAssets, "index-TESTFIXTURE.css");
        if (!File.Exists(bundledIndex))
        {
            Directory.CreateDirectory(bundledAssets);
            try
            {
                await File.WriteAllTextAsync(bundledIndex,
                    "<!doctype html><html><head><title>RC-09 fixture</title>" +
                    "<link rel=\"stylesheet\" href=\"/operator/console/assets/index-TESTFIXTURE.css\"></head>" +
                    "<body><div id=\"root\"></div>" +
                    "<script type=\"module\" src=\"/operator/console/assets/index-TESTFIXTURE.js\"></script>" +
                    "</body></html>");
                await File.WriteAllTextAsync(syntheticJs,
                    "/* RC-09 synthetic static fixture, no external requests */");
                await File.WriteAllTextAsync(syntheticCss,
                    "/* RC-09 synthetic CSS fixture */");
                using var reactIndex = await Get(uiClient, "/operator/console", operator1);
                Check(reactIndex.StatusCode == HttpStatusCode.OK
                    && (await reactIndex.Content.ReadAsStringAsync()).Contains(
                        "RC-09 fixture", StringComparison.Ordinal),
                    "local built-index route takes precedence over vanilla fallback");
                using var servedJs = await Get(uiClient,
                    "/operator/console/assets/index-TESTFIXTURE.js", operator1);
                Check(servedJs.StatusCode == HttpStatusCode.OK
                    && servedJs.Content.Headers.ContentType?.MediaType == "text/javascript"
                    && (await servedJs.Content.ReadAsStringAsync()).Contains(
                        "synthetic static fixture", StringComparison.Ordinal)
                    && servedJs.Headers.TryGetValues("Content-Security-Policy", out var jsCsp)
                    && jsCsp.Single().Contains("default-src 'none'", StringComparison.Ordinal),
                    "hashed JS asset is local and inherits restrictive CSP");
                using var servedCss = await Get(uiClient,
                    "/operator/console/assets/index-TESTFIXTURE.css", operator1);
                Check(servedCss.StatusCode == HttpStatusCode.OK
                    && servedCss.Content.Headers.ContentType?.MediaType == "text/css",
                    "hashed CSS asset is served on the same guarded origin");
            }
            finally
            {
                File.Delete(syntheticJs);
                File.Delete(syntheticCss);
                File.Delete(bundledIndex);
            }
        }
        else
        {
            // Actual Vite integration is checked separately by pinned SHA gate.
            Check(File.Exists(bundledIndex), "bundled index exists for external Vite gate");
        }


        using var missing = await Get(uiClient,
            "/operator/console/v1/proposals", null);
        Check(missing.StatusCode == HttpStatusCode.Unauthorized,
            "JWT required before any proposal details are read");
        using var invalid = await Get(uiClient,
            "/operator/console/v1/proposals", outsider);
        Check(invalid.StatusCode == HttpStatusCode.Forbidden,
            "unknown JWT tenant cannot read proposal queue");

        using var alphaList = await Get(uiClient,
            "/operator/console/v1/proposals", operator1);
        using var betaList = await Get(uiClient,
            "/operator/console/v1/proposals", operator2);
        Check(alphaList.StatusCode == HttpStatusCode.OK
            && betaList.StatusCode == HttpStatusCode.OK,
            "operator-specific console list is accessible");
        var alphaJson = await alphaList.Content.ReadAsStringAsync();
        var betaJson = await betaList.Content.ReadAsStringAsync();
        Check(alphaJson.Contains("ALPHA-WATER", StringComparison.Ordinal)
            && !alphaJson.Contains("BETA-ELEVATOR", StringComparison.Ordinal)
            && betaJson.Contains("BETA-ELEVATOR", StringComparison.Ordinal)
            && !betaJson.Contains("ALPHA-WATER", StringComparison.Ordinal),
            "console proposal lists are tenant-isolated");

        await CheckTotalTicketsAsync(adminConn!, 2,
            "preview cannot create a ticket");
        using var wrongTenant = await Post(uiClient,
            "/operator/console/v1/proposals/ALPHA-WATER/approve",
            """{"confirmation":"APPROVE ALPHA-WATER"}""", operator2);
        Check(wrongTenant.StatusCode == HttpStatusCode.NotFound,
            "cross-tenant approve does not disclose a proposal");
        using var missingConfirmation = await Post(uiClient,
            "/operator/console/v1/proposals/ALPHA-WATER/approve",
            """{"confirmation":"APPROVE BETA-ELEVATOR"}""", operator1);
        Check(missingConfirmation.StatusCode == HttpStatusCode.BadRequest,
            "wrong-case confirmation cannot persist");

        using var malformed = await Post(uiClient,
            "/operator/console/v1/proposals/ALPHA-WATER/approve",
            """{"confirmation":"APPROVE ALPHA-WATER","extra":true}""", operator1);
        Check(malformed.StatusCode == HttpStatusCode.BadRequest,
            "unexpected fields cannot influence approval");

        await CheckTotalTicketsAsync(adminConn!, 2,
            "unauthorized/malformed confirmation makes no database writes");
        using var approvedResponse = await Post(uiClient,
            "/operator/console/v1/proposals/ALPHA-WATER/approve",
            """{"confirmation":"APPROVE ALPHA-WATER"}""", operator1);
        Check(approvedResponse.StatusCode == HttpStatusCode.OK,
            "affirmative case-specific signed operator action accepted");
        await CheckTotalTicketsAsync(adminConn!, 3,
            "confirmed console action persists exactly one database ticket");

        using var duplicated = await Post(uiClient,
            "/operator/console/v1/proposals/ALPHA-WATER/approve",
            """{"confirmation":"APPROVE ALPHA-WATER"}""", operator1);
        Check(duplicated.StatusCode == HttpStatusCode.Conflict,
            "duplicate browser action fails closed (no double ticket)");
        await CheckTotalTicketsAsync(adminConn!, 3,
            "duplicate browser action cannot create second ticket");

        using var reject = await Post(uiClient,
            "/operator/console/v1/proposals/BETA-ELEVATOR/reject",
            """{"confirmation":"REJECT BETA-ELEVATOR"}""", operator2);
        Check(reject.StatusCode == HttpStatusCode.OK,
            "operator may reject own synthetic proposal");
        using var rejectedAgain = await Post(uiClient,
            "/operator/console/v1/proposals/BETA-ELEVATOR/approve",
            """{"confirmation":"APPROVE BETA-ELEVATOR"}""", operator2);
        Check(rejectedAgain.StatusCode == HttpStatusCode.Conflict,
            "rejected proposal cannot later be approved");
        await CheckTotalTicketsAsync(adminConn!, 3,
            "rejected item remains non-persistent");

        using var alphaAfter = await Get(uiClient,
            "/operator/console/v1/proposals", operator1);
        using var consoleViewJson = JsonDocument.Parse(
            await alphaAfter.Content.ReadAsStringAsync());
        var alphaItem = consoleViewJson.RootElement[0];
        Check(alphaItem.GetProperty("state").GetString() == "Approved"
            && alphaItem.GetProperty("ticketId").ValueKind == JsonValueKind.String,
            "operator can see persisted ticket reference and state");
        var consoleTicketId = alphaItem.GetProperty("ticketId").GetGuid();
        Check((await Get(uiClient, "/operator/v1/tickets/" + consoleTicketId,
            operator1)).StatusCode == HttpStatusCode.OK,
            "console-approved ticket status accessible through existing JWT operator API");
        Check((await Get(uiClient, "/operator/v1/tickets/" + consoleTicketId,
            operator2)).StatusCode == HttpStatusCode.NotFound,
            "console-approved ticket inaccessible to other tenant");
    }

    await VerifyDurableTelegramIntakeAsync(adminConn!, appConn!);
    await VerifyDurableOperatorProposalsAsync(adminConn!, appConn!);

    await VerifyRlsAsync(appConn!, Guid.Parse(Tenant1), Guid.Parse(Tenant2));
    Console.WriteLine("RC-02 PASS: JWT authorization, two-tenant HTTP isolation, PostgreSQL RLS and audit.");
    return 0;
}
catch (Exception error)
{
    // Deliberately never print payload, credentials, SQL parameters or identifiers.
    Console.Error.WriteLine("RC-02 FAIL: " + error.GetType().Name);
    return 1;
}


static async Task VerifyDurableOperatorProposalsAsync(
    string adminConnection, string runtimeConnection)
{
    const string proposalPath = "/operator/console/v1/proposals";
    var tenantA = Guid.Parse(Tenant1);
    var tenantB = Guid.Parse(Tenant2);
    var alphaJwt = Jwt(Tenant1, "operator", "synthetic-db-proposal-alpha");
    var betaJwt = Jwt(Tenant2, "operator", "synthetic-db-proposal-beta");

    async Task<long> AdminCount(string table)
    {
        // Only literal locally authored table identifiers passed to this helper.
        if (table is not ("operator_proposals" or "operator_proposal_decisions"
            or "tickets" or "ticket_audit"))
            throw new InvalidOperationException("Unknown test table");
        await using var db = new NpgsqlConnection(adminConnection);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM residentchat." + table, db);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    var ticketsBefore = await AdminCount("tickets");
    var auditBefore = await AdminCount("ticket_audit");
    await using (var server = await StartAsync(runtimeConnection, true, true,
        consoleEnabled: true, durableIntake: true, durableProposals: true))
    {
        var backend = server.GetRequiredService<PostgresDurableProposalStore>();
        using var initiallyEmpty = await Get(server.Client, proposalPath, alphaJwt);
        Check(initiallyEmpty.StatusCode == HttpStatusCode.OK
            && (await initiallyEmpty.Content.ReadAsStringAsync()).Trim() == "[]",
            "RC-10 durable mode never returns fictional in-memory queue");
        Check(await backend.CreateFromClassifiedAsync(tenantA, 900000,
            TicketCategory.Water, TicketPriority.Urgent, CancellationToken.None) is null,
            "classification cannot create a proposal without admitted Telegram metadata");
        Check(await backend.CreateFromClassifiedAsync(tenantB, 92001,
            TicketCategory.Water, TicketPriority.Urgent, CancellationToken.None) is null,
            "other tenant cannot propose from an ALPHA Telegram update");

        var alphaId = await backend.CreateFromClassifiedAsync(tenantA, 92001,
            TicketCategory.Water, TicketPriority.Urgent, CancellationToken.None);
        var betaId = await backend.CreateFromClassifiedAsync(tenantB, 92002,
            TicketCategory.Elevator, TicketPriority.Routine, CancellationToken.None);
        Check(alphaId.HasValue && betaId.HasValue,
            "independent trusted synthetic classification persists both tenant proposals");
        Check(await backend.CreateFromClassifiedAsync(tenantA, 92001,
            TicketCategory.Water, TicketPriority.Urgent, CancellationToken.None) is null,
            "classification is idempotent per admitted update");
        var alphaCase = PostgresDurableProposalStore.CaseId(alphaId!.Value);
        var betaCase = PostgresDurableProposalStore.CaseId(betaId!.Value);
        using var alphaQueue = await Get(server.Client, proposalPath, alphaJwt);
        using var betaQueue = await Get(server.Client, proposalPath, betaJwt);
        using var alphaJson = JsonDocument.Parse(await alphaQueue.Content.ReadAsStringAsync());
        using var betaJson = JsonDocument.Parse(await betaQueue.Content.ReadAsStringAsync());
        Check(alphaQueue.StatusCode == HttpStatusCode.OK
            && betaQueue.StatusCode == HttpStatusCode.OK
            && alphaJson.RootElement.GetArrayLength() == 1
            && betaJson.RootElement.GetArrayLength() == 1
            && alphaJson.RootElement[0].GetProperty("caseId").GetString() == alphaCase
            && betaJson.RootElement[0].GetProperty("caseId").GetString() == betaCase
            && alphaJson.RootElement[0].GetProperty("state").GetString() == "Pending",
            "React queue is real PostgreSQL and isolated by signed JWT tenant");
        using var anonymous = await Get(server.Client, proposalPath, null);
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized,
            "durable proposal queue requires a signed JWT");

        var alphaApprove = proposalPath + "/" + alphaCase + "/approve";
        using var wrongTenant = await Post(server.Client, alphaApprove,
            JsonSerializer.Serialize(new { confirmation = "APPROVE " + alphaCase }),
            betaJwt);
        Check(wrongTenant.StatusCode == HttpStatusCode.NotFound,
            "foreign tenant cannot approve another tenant's proposal");
        using var wrongConfirmation = await Post(server.Client, alphaApprove,
            JsonSerializer.Serialize(new { confirmation = "APPROVE " + betaCase }),
            alphaJwt);
        Check(wrongConfirmation.StatusCode == HttpStatusCode.BadRequest,
            "wrong exact confirmation cannot mutate a proposal");
        Check(await AdminCount("tickets") == ticketsBefore,
            "unapproved proposals must not create tickets");

        using var approved = await Post(server.Client, alphaApprove,
            JsonSerializer.Serialize(new { confirmation = "APPROVE " + alphaCase }),
            alphaJwt);
        Check(approved.StatusCode == HttpStatusCode.OK,
            "exact Alpha approval atomically commits real ticket and decision");
        using var repeated = await Post(server.Client, alphaApprove,
            JsonSerializer.Serialize(new { confirmation = "APPROVE " + alphaCase }),
            alphaJwt);
        Check(repeated.StatusCode == HttpStatusCode.Conflict,
            "repeated Alpha approval cannot insert a second ticket");
        using var rejected = await Post(server.Client,
            proposalPath + "/" + betaCase + "/reject",
            JsonSerializer.Serialize(new { confirmation = "REJECT " + betaCase }),
            betaJwt);
        Check(rejected.StatusCode == HttpStatusCode.OK,
            "Beta rejection commits a decision without ticket");
        using var rejectedApprove = await Post(server.Client,
            proposalPath + "/" + betaCase + "/approve",
            JsonSerializer.Serialize(new { confirmation = "APPROVE " + betaCase }),
            betaJwt);
        Check(rejectedApprove.StatusCode == HttpStatusCode.Conflict,
            "rejected Beta proposal cannot be approved afterward");

        Check(await AdminCount("tickets") == ticketsBefore + 1
            && await AdminCount("ticket_audit") == auditBefore + 1
            && await AdminCount("operator_proposals") == 2
            && await AdminCount("operator_proposal_decisions") == 2,
            "two human decisions produce exactly one ticket and atomic audit");

        using var updated = await Get(server.Client, proposalPath, alphaJwt);
        using var updatedJson = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        var committed = updatedJson.RootElement[0];
        var ticketId = committed.GetProperty("ticketId").GetGuid();
        Check(committed.GetProperty("state").GetString() == "Approved"
            && ticketId != Guid.Empty,
            "approved proposal references its durable PostgreSQL ticket");
        using var readback = await Get(server.Client,
            "/operator/v1/tickets/" + ticketId, alphaJwt);
        using var readbackJson = JsonDocument.Parse(
            await readback.Content.ReadAsStringAsync());
        Check(readback.StatusCode == HttpStatusCode.OK
            && readbackJson.RootElement.GetProperty("status").GetInt32()
                == (int)TicketStatus.Escalated
            && readbackJson.RootElement.GetProperty("version").GetInt32() == 1,
            "React ticket readback reports committed Urgent/Escalated version 1");
        using var foreignRead = await Get(server.Client,
            "/operator/v1/tickets/" + ticketId, betaJwt);
        Check(foreignRead.StatusCode == HttpStatusCode.NotFound,
            "tenant isolation applies to approved PostgreSQL ticket too");
    }

    // In-memory locks are gone: proposal decisions survive actual API restart.
    await using (var restarted = await StartAsync(runtimeConnection, true, true,
        consoleEnabled: true, durableIntake: true, durableProposals: true))
    {
        using var a = await Get(restarted.Client, proposalPath, alphaJwt);
        using var b = await Get(restarted.Client, proposalPath, betaJwt);
        var textA = await a.Content.ReadAsStringAsync();
        var textB = await b.Content.ReadAsStringAsync();
        Check(textA.Contains("\"Approved\"", StringComparison.Ordinal)
            && textB.Contains("\"Rejected\"", StringComparison.Ordinal)
            && !textA.Contains("\"ALPHA-WATER\"", StringComparison.Ordinal),
            "database-proposal lifecycle persists across process restart");
    }

    await using (var db = new NpgsqlConnection(runtimeConnection))
    {
        await db.OpenAsync();
        await using (var deny = new NpgsqlCommand(
            "SELECT count(*) FROM residentchat.operator_proposals", db))
            Check((long)(await deny.ExecuteScalarAsync())! == 0,
                "proposal PostgreSQL RLS default denies missing tenant");
        await using var tx = await db.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand(
            "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx))
        {
            set.Parameters.AddWithValue("tenant", Tenant1);
            await set.ExecuteNonQueryAsync();
        }
        await using (var count = new NpgsqlCommand(
            "SELECT count(*) FROM residentchat.operator_proposals", db, tx))
            Check((long)(await count.ExecuteScalarAsync())! == 1,
                "RLS SQL directly hides Beta proposal from Alpha");
        await tx.RollbackAsync();
    }
    Console.WriteLine("RC-10 DURABLE PG PASS: approval, reject, restart, JWT, RLS and atomic audit.");
}

static async Task VerifyDurableTelegramIntakeAsync(
    string adminConnection, string runtimeConnection)
{
    const string textValue = "TEST_PRIVATE_RESIDENT_TEXT_MUST_NOT_BE_STORED";
    var alpha = JsonSerializer.Serialize(new {
        update_id = 92001L,
        message = new {
            chat = new { id = AlphaChat, type = "supergroup" },
            text = textValue
        }
    });
    var beta = JsonSerializer.Serialize(new {
        update_id = 92002L,
        message = new {
            chat = new { id = BetaChat, type = "supergroup" },
            text = "synthetic elevator request"
        }
    });
    await using (var incorrectlyMapped =
        await StartAsync(runtimeConnection, true, true, durableIntake: true, badGroupMap: true))
    {
        using var failed = await PostWebhook(incorrectlyMapped.Client, alpha);
        Check(failed.StatusCode == HttpStatusCode.ServiceUnavailable,
            "incomplete tenant/group mapping refuses all durable admissions");
    }

    await using (var first =
        await StartAsync(runtimeConnection, true, true,
            consoleEnabled: true, durableIntake: true))
    {
        using var alphaFirst = await PostWebhook(first.Client, alpha);
        Check(alphaFirst.StatusCode == HttpStatusCode.OK
            && (await alphaFirst.Content.ReadAsStringAsync()).Contains(
                "\"accepted\"", StringComparison.Ordinal),
            "real PG transaction commits before webhook 200");
        using var alphaRepeat = await PostWebhook(first.Client, alpha);
        Check(alphaRepeat.StatusCode == HttpStatusCode.OK
            && (await alphaRepeat.Content.ReadAsStringAsync()).Contains(
                "\"duplicate\"", StringComparison.Ordinal),
            "Telegram retry is idempotent within same server process");
        using var betaFirst = await PostWebhook(first.Client, beta);
        Check(betaFirst.StatusCode == HttpStatusCode.OK,
            "second tenant has a separate real PostgreSQL durable intake");

        // Read a REAL committed PostgreSQL tenant queue using the same
        // authorization and loopback rules consumed by the React component.
        using var alphaVisible = await Get(first.Client,
            "/operator/console/v1/intake",
            Jwt(Tenant1, "operator", "synthetic-intake-alpha"));
        using var betaVisible = await Get(first.Client,
            "/operator/console/v1/intake",
            Jwt(Tenant2, "operator", "synthetic-intake-beta"));
        Check(alphaVisible.StatusCode == HttpStatusCode.OK
            && betaVisible.StatusCode == HttpStatusCode.OK,
            "signed operator metadata GET reads committed PostgreSQL");
        var alphaData = await alphaVisible.Content.ReadAsStringAsync();
        var betaData = await betaVisible.Content.ReadAsStringAsync();
        using var alphaJson = JsonDocument.Parse(alphaData);
        using var betaJson = JsonDocument.Parse(betaData);
        Check(alphaJson.RootElement.GetArrayLength() == 1
            && betaJson.RootElement.GetArrayLength() == 1
            && alphaJson.RootElement[0].GetProperty("updateId").GetInt64() == 92001
            && betaJson.RootElement[0].GetProperty("updateId").GetInt64() == 92002
            && !alphaData.Contains(textValue, StringComparison.Ordinal)
            && !alphaData.Contains("chatHash", StringComparison.OrdinalIgnoreCase),
            "React metadata readback is tenant-isolated and contains no raw text or chat hash");
        using var anonymous = await Get(first.Client,
            "/operator/console/v1/intake", null);
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized,
            "unsigned browser request cannot read real PG intake");
        using var foreign = await PostWebhook(first.Client,
            """{"update_id":92003,"message":{"chat":{"id":-1009999999999,"type":"supergroup"},"text":"ignored"}}""");
        Check(foreign.StatusCode == HttpStatusCode.OK
            && (await foreign.Content.ReadAsStringAsync()).Contains(
                "\"ignored\"", StringComparison.Ordinal),
            "unknown Telegram group never persists");
    }

    // Process restarted, in-memory dedup lost. PostgreSQL unique key persists.
    await using (var restarted =
        await StartAsync(runtimeConnection, true, true,
            consoleEnabled: true, durableIntake: true))
    {
        using var repeatAfterRestart = await PostWebhook(restarted.Client, alpha);
        Check(repeatAfterRestart.StatusCode == HttpStatusCode.OK
            && (await repeatAfterRestart.Content.ReadAsStringAsync()).Contains(
                "\"duplicate\"", StringComparison.Ordinal),
            "PostgreSQL deduplication survives API restart");
    }

    await using (var db = new NpgsqlConnection(adminConnection))
    {
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT tenant_id, update_id, chat_hash, state
              FROM residentchat.telegram_intake ORDER BY update_id
            """, db);
        await using var reader = await cmd.ExecuteReaderAsync();
        Check(await reader.ReadAsync()
            && reader.GetGuid(0) == Guid.Parse(Tenant1)
            && reader.GetInt64(1) == 92001
            && reader.GetString(2).Length == 64
            && reader.GetInt16(3) == 1,
            "only first tenant's metadata is stored (no resident text)");
        Check(await reader.ReadAsync()
            && reader.GetGuid(0) == Guid.Parse(Tenant2)
            && reader.GetInt64(1) == 92002
            && reader.GetString(2).Length == 64
            && reader.GetInt16(3) == 1
            && !await reader.ReadAsync(),
            "exactly two metadata rows: no duplicate, foreign-group or hidden writes");
    }

    await using (var db = new NpgsqlConnection(runtimeConnection))
    {
        await db.OpenAsync();
        await using (var deny = new NpgsqlCommand(
            "SELECT count(*) FROM residentchat.telegram_intake", db))
            Check((long)(await deny.ExecuteScalarAsync())! == 0,
                "new table RLS default denies runtime without tenant scope");
        await using var tx = await db.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand(
            "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx))
        {
            set.Parameters.AddWithValue("tenant", Tenant1);
            await set.ExecuteNonQueryAsync();
        }
        await using (var one = new NpgsqlCommand(
            "SELECT count(*) FROM residentchat.telegram_intake", db, tx))
            Check((long)(await one.ExecuteScalarAsync())! == 1,
                "RLS tenant Alpha cannot see Beta intake");
        await tx.RollbackAsync();
    }
    Console.WriteLine("RC-10 PG PASS: durable Telegram ACK/dedup/restart, group mapping, RLS.");
}

static async Task<HttpResponseMessage> PostWebhook(HttpClient client, string json)
{
    using var message = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
    message.Headers.TryAddWithoutValidation(
        "X-Telegram-Bot-Api-Secret-Token", SyntheticTelegramSecret);
    message.Content = new StringContent(json, Encoding.UTF8, "application/json");
    return await client.SendAsync(message);
}

static async Task CheckTotalTicketsAsync(string adminConnection,
    long expected, string label)
{
    // Verification uses only the ephemeral migration owner supplied to the
    // synthetic lab. Application reads MUST continue using the RLS role.
    await using var db = new NpgsqlConnection(adminConnection);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT count(*) FROM residentchat.tickets", db);
    Check((long)(await cmd.ExecuteScalarAsync())! == expected, label);
}

static async Task SetupDatabaseAsync(string connection)
{
    await using var db = new NpgsqlConnection(connection);
    await db.OpenAsync();
    var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "migrations", "001_ticket_workflow.sql"));
    await using (var cmd = new NpgsqlCommand(migration, db))
        await cmd.ExecuteNonQueryAsync();

    var intakeMigration = await File.ReadAllTextAsync(
        Path.Combine(AppContext.BaseDirectory, "migrations", "003_telegram_intake.sql"));
    await using (var cmd = new NpgsqlCommand(intakeMigration, db))
        await cmd.ExecuteNonQueryAsync();

    var proposalMigration = await File.ReadAllTextAsync(
        Path.Combine(AppContext.BaseDirectory, "migrations", "004_operator_proposals.sql"));
    await using (var cmd = new NpgsqlCommand(proposalMigration, db))
        await cmd.ExecuteNonQueryAsync();

    await using (var grant = new NpgsqlCommand("""
        DO $do$
        BEGIN
            IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'residentchat_synthetic_app') THEN
                CREATE ROLE residentchat_synthetic_app LOGIN PASSWORD 'synthetic-only-pass';
            END IF;
        END
        $do$;
        GRANT USAGE ON SCHEMA residentchat TO residentchat_synthetic_app;
        GRANT SELECT, INSERT, UPDATE ON residentchat.tickets TO residentchat_synthetic_app;
        GRANT SELECT, INSERT ON residentchat.telegram_intake TO residentchat_synthetic_app;
        GRANT SELECT, INSERT, UPDATE ON residentchat.operator_proposals TO residentchat_synthetic_app;
        GRANT SELECT, INSERT ON residentchat.operator_proposal_decisions TO residentchat_synthetic_app;
        GRANT SELECT, INSERT ON residentchat.ticket_audit TO residentchat_synthetic_app;
        GRANT USAGE, SELECT ON SEQUENCE residentchat.ticket_audit_id_seq
            TO residentchat_synthetic_app;
        """, db))
        await grant.ExecuteNonQueryAsync();
}

static async Task VerifyRlsAsync(string connection, Guid tenant1, Guid tenant2)
{
    await using var db = new NpgsqlConnection(connection);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand("SELECT count(*) FROM residentchat.tickets", db);
    var withoutTenant = (long)(await cmd.ExecuteScalarAsync())!;
    Check(withoutTenant == 0, "RLS default deny without session tenant");

    await using var tx = await db.BeginTransactionAsync();
    await using var set = new NpgsqlCommand(
        "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx);
    set.Parameters.AddWithValue("tenant", tenant1.ToString("D"));
    await set.ExecuteNonQueryAsync();

    await using var count = new NpgsqlCommand(
        "SELECT count(*) FROM residentchat.tickets WHERE tenant_id = @tenant", db, tx);
    count.Parameters.AddWithValue("tenant", tenant2);
    var otherCount = (long)(await count.ExecuteScalarAsync())!;
    Check(otherCount == 0, "DB role cannot read other tenant with RLS");
    await tx.RollbackAsync();
}

static async Task<HttpResponseMessage> Post(HttpClient client,
    string path, string payload, string? jwt)
{
    using var req = new HttpRequestMessage(HttpMethod.Post, path);
    req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
    if (jwt is not null)
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
    return await client.SendAsync(req);
}

static async Task<HttpResponseMessage> Get(HttpClient client, string path, string? jwt)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, path);
    if (jwt is not null)
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
    return await client.SendAsync(req);
}

static string Jwt(string tenantId, string role, string subject)
{
    var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
    var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new {
        iss = Issuer, aud = Audience, sub = subject, tenant_id = tenantId, role,
        exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()
    }));
    var signed = header + "." + payload;
    var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningKey),
        Encoding.ASCII.GetBytes(signed));
    return signed + "." + Encode(signature);
}

static string Encode(byte[] input) =>
    Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException(label);
}

static async Task<SyntheticServer> StartAsync(
    string connection, bool enabled, bool configured, bool consoleEnabled = false,
    bool durableIntake = false, bool badGroupMap = false,
    bool durableProposals = false)
{
    var overrides = new Dictionary<string, string?>
    {
        ["ResidentChat:Operator:Enabled"] = enabled.ToString(),
        ["ResidentChat:Operator:Issuer"] = Issuer,
        ["ResidentChat:Operator:Audience"] = Audience,
        ["ResidentChat:Operator:JwtSigningKey"] = configured ? SigningKey : "",
        ["ResidentChat:Operator:AuditHmacKey"] = AuditKey,
        ["ResidentChat:Operator:ConnectionString"] = connection,
        ["ResidentChat:Operator:AllowedTenantIds:0"] = Tenant1,
        ["ResidentChat:Operator:AllowedTenantIds:1"] = Tenant2,
        ["ResidentChat:OperatorConsole:Enabled"] = consoleEnabled.ToString(),
        ["ResidentChat:OperatorConsole:Mode"] = consoleEnabled ? "synthetic-lab-only" : ""
    };
    if (durableIntake)
    {
        overrides["ResidentChat:Telegram:Enabled"] = "true";
        overrides["ResidentChat:Telegram:WebhookSecret"] = SyntheticTelegramSecret;
        overrides["ResidentChat:Telegram:AllowedChatIds:0"] = AlphaChat.ToString();
        overrides["ResidentChat:Telegram:AllowedChatIds:1"] = BetaChat.ToString();
        overrides["ResidentChat:DurableIntake:Enabled"] = "true";
        overrides["ResidentChat:DurableIntake:Mode"] = "metadata-only-test-groups";
        overrides["ResidentChat:DurableIntake:ChatHashHmacKey"] = SyntheticHmacKey;
        overrides["ResidentChat:DurableIntake:Groups:0:ChatId"] = AlphaChat.ToString();
        overrides["ResidentChat:DurableIntake:Groups:0:TenantId"] = Tenant1;
        overrides["ResidentChat:DurableIntake:Groups:1:ChatId"] = BetaChat.ToString();
        overrides["ResidentChat:DurableIntake:Groups:1:TenantId"] =
            badGroupMap ? Outsider : Tenant2;
    }
    if (durableProposals)
    {
        overrides["ResidentChat:DurableProposals:Enabled"] = "true";
        overrides["ResidentChat:DurableProposals:Mode"] = "synthetic-postgres-only";
    }
    var app = ResidentChatApp.Build([], overrides);
    app.Urls.Add("http://127.0.0.1:0");
    await app.StartAsync();
    var server = app.Services.GetRequiredService<IServer>();
    var urls = server.Features.Get<IServerAddressesFeature>()
        ?? throw new InvalidOperationException("Missing synthetic listener");
    return new SyntheticServer(app, new HttpClient {
        BaseAddress = new Uri(urls.Addresses.Single(
            a => a.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)))
    });
}

internal sealed class SyntheticServer(WebApplication app, HttpClient client) : IAsyncDisposable
{
    public HttpClient Client { get; } = client;
    public T GetRequiredService<T>() where T : notnull => app.Services.GetRequiredService<T>();
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
