using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using ResidentChat.Api;

// RC-25: one-shot real ResidentChat HTTP API -> real PERMANENT PostgreSQL.
// One harmless metadata-only ticket in a reserved lab tenant is retained
// after the test; an already existing fixture is REUSED on subsequent runs.
// No resident messages, Telegram updates, local LLM, external API, schemas,
// migrations, bot tokens, or actual user tenants are touched.
const string TenantAlpha = "25252525-2525-4525-8525-252525252525";
const string TenantBeta = "26262626-2626-4626-8626-262626262626";
const string Issuer = "residentchat.rc25.local-only";
const string Audience = "residentchat.rc25.synthetic-only";
const string SecretPath = "/run/secrets/pg_runtime";
try
{
    if (args.Length != 1 || args[0] != "--persistent-api-once")
        throw new InvalidOperationException("RC25 only permits explicit one-shot test.");
    if (!File.Exists(SecretPath))
        throw new InvalidOperationException("RC25 runtime password file missing.");
    string password = (await File.ReadAllTextAsync(SecretPath)).Trim();
    if (password.Length != 64 || password.Any(c => !Uri.IsHexDigit(c)))
        throw new InvalidOperationException("RC25 password format denied.");

    var builder = new NpgsqlConnectionStringBuilder {
        Host = "/var/run/postgresql",
        Port = 5432,
        Database = "residentchat",
        Username = "residentchat_lab_runtime",
        Password = password,
        Pooling = false,
        SslMode = SslMode.Disable,
        Timeout = 5,
        CommandTimeout = 10,
        ApplicationName = "residentchat-rc25-one-shot-api"
    };
    string connectionString = builder.ConnectionString;
    Guid alphaId = Guid.Parse(TenantAlpha);
    string signingKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(48));
    string auditKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(48));
    string alphaJwt = Jwt(TenantAlpha, "rc25-synthetic-alpha", signingKey);
    string betaJwt = Jwt(TenantBeta, "rc25-synthetic-beta", signingKey);

    // All reads in the existing DB are tenant-scoped via transaction SET LOCAL.
    // Never modify another tenant. No global table counts.
    async Task<(Guid? Id, long TicketCount, long AuditCount)> InspectAsync()
    {
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('residentchat.tenant_id', @tenant, true)", db, tx))
        {
            scope.Parameters.AddWithValue("tenant", NpgsqlDbType.Text, TenantAlpha);
            await scope.ExecuteNonQueryAsync();
        }
        Guid? id;
        long count;
        await using (var query = new NpgsqlCommand("""
            SELECT ticket_id FROM residentchat.tickets
            WHERE tenant_id = @tenant ORDER BY created_at, ticket_id LIMIT 2
            """, db, tx))
        {
            query.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, alphaId);
            await using var reader = await query.ExecuteReaderAsync();
            id = await reader.ReadAsync() ? reader.GetGuid(0) : null;
            count = id is null ? 0 : 1;
            if (await reader.ReadAsync()) count = 2;
        }
        long audit;
        await using (var query = new NpgsqlCommand("""
            SELECT count(*) FROM residentchat.ticket_audit
            WHERE tenant_id = @tenant
            """, db, tx))
        {
            query.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, alphaId);
            audit = (long)(await query.ExecuteScalarAsync())!;
        }
        await tx.CommitAsync();
        return (id, count, audit);
    }

    var initial = await InspectAsync();
    Require(initial.TicketCount <= 1 && initial.AuditCount <= 1
        && initial.TicketCount == initial.AuditCount,
        "Only one dedicated RC25 test fixture is permitted");
    Guid? ticketId = initial.Id;

    async Task<(WebApplication App, HttpClient Http)> StartApiAsync()
    {
        var config = new Dictionary<string, string?> {
            ["ResidentChat:Operator:Enabled"] = "true",
            ["ResidentChat:Operator:Issuer"] = Issuer,
            ["ResidentChat:Operator:Audience"] = Audience,
            ["ResidentChat:Operator:JwtSigningKey"] = signingKey,
            ["ResidentChat:Operator:AuditHmacKey"] = auditKey,
            ["ResidentChat:Operator:ConnectionString"] = connectionString,
            ["ResidentChat:Operator:AllowedTenantIds:0"] = TenantAlpha,
            ["ResidentChat:Operator:AllowedTenantIds:1"] = TenantBeta,
            ["ResidentChat:Telegram:Enabled"] = "false",
            ["ResidentChat:TelegramPolling:Enabled"] = "false",
            ["ResidentChat:DurableIntake:Enabled"] = "false",
            ["ResidentChat:ClassifiedTelegram:Enabled"] = "false",
            ["ResidentChat:OperatorConsole:Enabled"] = "false"
        };
        var api = ResidentChatApp.Build([], config);
        api.Urls.Add("http://127.0.0.1:0");
        await api.StartAsync();
        var server = api.Services.GetRequiredService<IServer>();
        var url = server.Features.Get<IServerAddressesFeature>()!
            .Addresses.Single(s => s.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
        return (api, new HttpClient { BaseAddress = new Uri(url),
            Timeout = TimeSpan.FromSeconds(12) });
    }

    async Task<HttpResponseMessage> GetAsync(HttpClient http, Guid id, string? jwt)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "/operator/v1/tickets/" + id.ToString("D"));
        if (jwt is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return await http.SendAsync(req);
    }

    var first = await StartApiAsync();
    try
    {
        using var health = await first.Http.GetAsync("/health");
        Require(health.StatusCode == HttpStatusCode.OK, "Real ASP.NET HTTP endpoint");

        if (ticketId is null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/operator/v1/tickets");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", alphaJwt);
            req.Content = new StringContent(
                """{"category":"Water","priority":"Routine"}""",
                Encoding.UTF8, "application/json");
            using var created = await first.Http.SendAsync(req);
            Require(created.StatusCode == HttpStatusCode.Created,
                "JWT-authorized synthetic ticket persisted by real API");
            using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            ticketId = doc.RootElement.GetProperty("ticketId").GetGuid();
            Require(doc.RootElement.GetProperty("tenantId").GetGuid() == alphaId,
                "Synthetic fixture cannot change tenant scope");
            Console.WriteLine("RC25_NEW_SYNTHETIC_METADATA_TICKET_CREATED=YES");
        }
        else
        {
            Console.WriteLine("RC25_EXISTING_SYNTHETIC_FIXTURE_REUSED=YES");
        }

        using var ok = await GetAsync(first.Http, ticketId.Value, alphaJwt);
        using var foreign = await GetAsync(first.Http, ticketId.Value, betaJwt);
        using var anonymous = await GetAsync(first.Http, ticketId.Value, null);
        Require(ok.StatusCode == HttpStatusCode.OK
            && foreign.StatusCode == HttpStatusCode.NotFound
            && anonymous.StatusCode == HttpStatusCode.Unauthorized,
            "JWT tenant isolation and anonymous deny");
    }
    finally
    {
        first.Http.Dispose();
        await first.App.StopAsync();
        await first.App.DisposeAsync();
    }
    Console.WriteLine("RC25_FIRST_API_INSTANCE_STOPPED=YES");

    var second = await StartApiAsync();
    try
    {
        using var own = await GetAsync(second.Http, ticketId!.Value, alphaJwt);
        using var other = await GetAsync(second.Http, ticketId.Value, betaJwt);
        Require(own.StatusCode == HttpStatusCode.OK
            && other.StatusCode == HttpStatusCode.NotFound,
            "Ticket survived full API process lifecycle with PostgreSQL RLS");
        using var doc = JsonDocument.Parse(await own.Content.ReadAsStringAsync());
        Require(doc.RootElement.GetProperty("ticketId").GetGuid() == ticketId.Value
            && doc.RootElement.GetProperty("tenantId").GetGuid() == alphaId
            && doc.RootElement.GetProperty("version").GetInt32() == 1,
            "Stable ticket id and version after restart");
    }
    finally
    {
        second.Http.Dispose();
        await second.App.StopAsync();
        await second.App.DisposeAsync();
    }
    var after = await InspectAsync();
    Require(after.TicketCount == 1 && after.AuditCount == 1
        && after.Id == ticketId, "Exactly one synthetic ticket/audit row durable");
    Console.WriteLine("RC25_REAL_ASPNET10_HTTP_TO_PERSISTENT_POSTGRES_PASS");
    Console.WriteLine("RC25_API_RESTART_RETENTION_PASS");
    Console.WriteLine("RC25_FOREIGN_TENANT_JWT_RLS_DENY_PASS");
    Console.WriteLine("RC25_SYNTHETIC_TICKET_COUNT=1");
    Console.WriteLine("RC25_SYNTHETIC_AUDIT_COUNT=1");
    Console.WriteLine("RC25_DB_CONTAINER_RESTARTED=NO");
    Console.WriteLine("RC25_TELEGRAM_POLLING=OFF");
    Console.WriteLine("RC25_TELEGRAM_UPDATES_ACKED=NO");
    Console.WriteLine("RC25_PERSISTENT_API_ACCEPTANCE_PASS");
    return 0;
}
catch
{
    // Never print Npgsql exception texts or any connection-string secrets.
    Console.Error.WriteLine("RC25_FAIL_SAFE");
    return 1;
}

static string Jwt(string tenant, string subject, string signingKey)
{
    var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
    var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new {
        iss = "residentchat.rc25.local-only",
        aud = "residentchat.rc25.synthetic-only",
        sub = subject,
        tenant_id = tenant,
        role = "operator",
        exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()
    }));
    string signed = header + "." + payload;
    return signed + "." + Encode(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(signingKey), Encoding.ASCII.GetBytes(signed)));
}
static string Encode(byte[] x) =>
    Convert.ToBase64String(x).TrimEnd('=').Replace('+', '-').Replace('/', '_');
static void Require(bool value, string reason)
{
    if (!value) throw new InvalidOperationException(reason);
}
