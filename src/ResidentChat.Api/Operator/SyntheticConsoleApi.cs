using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Npgsql;
using ResidentChat.Api.Operator;
using ResidentChat.Core.Demo;
using ResidentChat.Core.LocalAi;

namespace ResidentChat.Api.Operator;

/// <summary>
/// Opt-in synthetic laboratory UI, deliberately separate from any live Telegram
/// ingress, local model execution or resident-data pipeline. Every mutating
/// operation still authenticates an operator JWT and checks tenant identity.
/// </summary>
public static class SyntheticConsoleApi
{
    private const int MaxConfirmationBytes = 160;

    public static void Register(WebApplicationBuilder builder)
    {
        var config = builder.Configuration.GetSection("ResidentChat:OperatorConsole");
        var enabled = config.GetValue<bool>("Enabled")
            && string.Equals(config["Mode"], "synthetic-lab-only", StringComparison.Ordinal);
        builder.Services.AddSingleton(new SyntheticConsoleOptions(enabled));
        builder.Services.AddSingleton<SyntheticConsoleInbox>();
    }

    public static void Map(WebApplication app)
    {
        var config = app.Services.GetRequiredService<SyntheticConsoleOptions>();
        var operatorConfig = app.Services.GetRequiredService<OperatorOptions>();

        // Fail closed unless two *independent* explicit opt-ins are configured:
        // fully ready signed-JWT operator API, and synthetic-only console.
        if (!config.Enabled || !operatorConfig.Ready)
            return;

        // Prefer the production Vite bundle when explicitly generated and
        // packaged with .NET. The original RC-06 HTML stays as a synthetic
        // fallback until the independent React build/acceptance gate passes.
        // Neither route can serve non-loopback requests.
        app.MapGet("/operator/console", (HttpContext ctx) =>
        {
            if (!IsDirectLoopback(ctx)) return Results.NotFound();
            SetBrowserSecurityHeaders(ctx);
            var reactIndex = Path.Combine(AppContext.BaseDirectory, "operator-ui", "index.html");
            return File.Exists(reactIndex)
                ? Results.File(reactIndex, "text/html; charset=utf-8")
                : Results.Content(SyntheticConsolePage.Html, "text/html; charset=utf-8");
        });

        // Serve ONLY local Vite hash-named JS/CSS assets, with no static file
        // middleware, directory traversal, CDN or cross-origin permissions.
        app.MapGet("/operator/console/assets/{assetName}", (string assetName, HttpContext ctx) =>
        {
            if (!IsDirectLoopback(ctx)) return Results.NotFound();
            if (!System.Text.RegularExpressions.Regex.IsMatch(assetName,
                @"^index-[a-zA-Z0-9_-]+[.](js|css)$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                return Results.NotFound();
            var asset = Path.Combine(AppContext.BaseDirectory, "operator-ui", "assets", assetName);
            if (!File.Exists(asset)) return Results.NotFound();
            SetBrowserSecurityHeaders(ctx);
            return Results.File(asset, assetName.EndsWith(".js", StringComparison.Ordinal)
                ? "text/javascript; charset=utf-8" : "text/css; charset=utf-8");
        });

        app.MapGet("/operator/console/app.js", (HttpContext ctx) =>
        {
            if (!IsDirectLoopback(ctx)) return Results.NotFound();
            SetBrowserSecurityHeaders(ctx);
            return Results.Content(SyntheticConsolePage.JavaScript, "text/javascript; charset=utf-8");
        });

        app.MapGet("/operator/console/app.css", (HttpContext ctx) =>
        {
            if (!IsDirectLoopback(ctx)) return Results.NotFound();
            SetBrowserSecurityHeaders(ctx);
            return Results.Content(SyntheticConsolePage.Css, "text/css; charset=utf-8");
        });

        var group = app.MapGroup("/operator/console/v1").RequireAuthorization();
        group.MapGet("/proposals", async (HttpContext ctx,
            SyntheticConsoleInbox inbox, OperatorOptions options) =>
        {
            if (!IsDirectLoopback(ctx)) return Results.NotFound();
            if (!OperatorAccess.TryGetScope(ctx.User, options, out var tenant, out _))
                return Results.Forbid();

            var items = await inbox.GetAsync(tenant, options, ctx.RequestAborted);
            SetNoStore(ctx);
            return Results.Ok(items);
        });

        group.MapPost("/proposals/{caseId}/approve",
            (string caseId, HttpContext ctx, SyntheticConsoleInbox inbox,
                OperatorOptions options, PostgresTicketStore store) =>
                ChangeAsync(caseId, true, ctx, inbox, options, store));
        group.MapPost("/proposals/{caseId}/reject",
            (string caseId, HttpContext ctx, SyntheticConsoleInbox inbox,
                OperatorOptions options, PostgresTicketStore store) =>
                ChangeAsync(caseId, false, ctx, inbox, options, store));
    }

    private static async Task<IResult> ChangeAsync(string caseId, bool approve,
        HttpContext ctx, SyntheticConsoleInbox inbox, OperatorOptions options,
        PostgresTicketStore store)
    {
        if (!IsDirectLoopback(ctx)) return Results.NotFound();
        if (!OperatorAccess.TryGetScope(ctx.User, options, out var tenant, out var actor))
            return Results.Forbid();

        var (confirmation, error) = await ReadConfirmationAsync(ctx);
        if (error is not null) return error;

        SetNoStore(ctx);
        var result = await inbox.ChangeAsync(tenant, options, caseId,
            confirmation!, approve, actor, store, ctx.RequestAborted);
        return result switch
        {
            ConsoleChangeResult.Ok => Results.Ok(new { status = approve ? "Approved" : "Rejected" }),
            ConsoleChangeResult.Missing => Results.NotFound(),
            ConsoleChangeResult.Conflict => Results.Conflict(new { error = "already_processed" }),
            ConsoleChangeResult.Invalid => Results.BadRequest(),
            _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        };
    }

    private static async Task<(string? Confirmation, IResult? Error)> ReadConfirmationAsync(HttpContext ctx)
    {
        if (ctx.Request.ContentType is null ||
            !ctx.Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        if (ctx.Request.ContentLength > MaxConfirmationBytes)
            return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));

        var buffer = new byte[MaxConfirmationBytes + 1];
        var offset = 0;
        try
        {
            while (offset < buffer.Length)
            {
                var count = await ctx.Request.Body.ReadAsync(
                    buffer.AsMemory(offset), ctx.RequestAborted);
                if (count == 0) break;
                offset += count;
            }
            if (offset > MaxConfirmationBytes)
                return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
            using var json = JsonDocument.Parse(buffer.AsMemory(0, offset),
                new JsonDocumentOptions { MaxDepth = 3 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("confirmation", out var property) ||
                property.ValueKind != JsonValueKind.String)
                return (null, Results.BadRequest());
            var value = property.GetString();
            return value is { Length: > 0 and <= 64 }
                ? (value, null) : (null, Results.BadRequest());
        }
        catch (JsonException) { return (null, Results.BadRequest()); }
        catch (BadHttpRequestException ex) when (
            ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer); }
    }

    private static bool IsDirectLoopback(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)
        && ctx.Connection.LocalIpAddress is { } local && IPAddress.IsLoopback(local);

    private static void SetNoStore(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private static void SetBrowserSecurityHeaders(HttpContext ctx)
    {
        SetNoStore(ctx);
        ctx.Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; script-src 'self'; style-src 'self'; " +
            "connect-src 'self'; img-src 'none'; base-uri 'none'; " +
            "form-action 'none'; frame-ancestors 'none'";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
    }
}

public sealed record SyntheticConsoleOptions(bool Enabled);
public sealed record SyntheticConsoleItem(
    string CaseId, string Category, string Priority, string State,
    Guid? TicketId = null);
public enum ConsoleChangeResult { Ok, Missing, Invalid, Conflict, Unavailable }

/// <summary>
/// Volatile, synthetic-only, tenant-bound inbox. Entire state disappears at
/// shutdown. A failure during write is IN_DOUBT, not automatically retried.
/// </summary>
public sealed class SyntheticConsoleInbox
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Dictionary<string, State>> _byTenant = [];

    private sealed class State(string caseId, DemoDispatchResult proposal)
    {
        public string CaseId { get; } = caseId;
        public DemoDispatchResult Proposal { get; } = proposal;
        public string Status { get; set; } = "Pending";
        public Guid? TicketId { get; set; }
        public SyntheticConsoleItem Snapshot() => new(CaseId,
            Proposal.Proposal?.Category.ToString() ?? "Other",
            Proposal.Proposal?.Priority.ToString() ?? "Routine",
            Status, TicketId);
    }

    private Dictionary<string, State> ForTenant(Guid tenant, OperatorOptions options)
    {
        if (_byTenant.TryGetValue(tenant, out var existing)) return existing;
        var permitted = options.AllowedTenantIds.OrderBy(x => x).ToArray();
        var items = new Dictionary<string, State>(StringComparer.Ordinal);
        if (permitted.Length > 0 && tenant == permitted[0])
            items["ALPHA-WATER"] = new State("ALPHA-WATER",
                DemoDispatchPolicy.Plan(tenant, new ResidentDecision(
                    true, ResidentIntent.Maintenance, "water",
                    ResidentUrgency.Urgent, true)));
        if (permitted.Length > 1 && tenant == permitted[1])
            items["BETA-ELEVATOR"] = new State("BETA-ELEVATOR",
                DemoDispatchPolicy.Plan(tenant, new ResidentDecision(
                    true, ResidentIntent.Maintenance, "elevator",
                    ResidentUrgency.Routine, false)));
        _byTenant[tenant] = items;
        return items;
    }

    public async Task<IReadOnlyList<SyntheticConsoleItem>> GetAsync(
        Guid tenant, OperatorOptions options, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return ForTenant(tenant, options).Values.Select(x => x.Snapshot()).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<ConsoleChangeResult> ChangeAsync(Guid tenant,
        OperatorOptions options, string caseId, string confirmation, bool approve,
        string actor, PostgresTicketStore store, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (tenant == Guid.Empty || !options.IsAllowedTenant(tenant)
                || !ForTenant(tenant, options).TryGetValue(caseId, out var state))
                return ConsoleChangeResult.Missing;
            if (state.Status != "Pending")
                return ConsoleChangeResult.Conflict;

            if (!approve)
            {
                if (!string.Equals(confirmation, "REJECT " + caseId,
                    StringComparison.Ordinal))
                    return ConsoleChangeResult.Invalid;
                state.Status = "Rejected";
                return ConsoleChangeResult.Ok;
            }

            if (!DemoApprovalPolicy.TryApprove(tenant, caseId,
                state.Proposal, confirmation, out var command) || command is null)
                return ConsoleChangeResult.Invalid;

            try
            {
                // This is the ONLY write. Real PostgreSQL, tenant-scope and
                // pseudonymized operator audit are implemented in RC-02.
                var ticket = await store.CreateAsync(tenant, command.Category,
                    command.Priority, actor, ct);
                state.TicketId = ticket.TicketId;
                state.Status = "Approved";
                return ConsoleChangeResult.Ok;
            }
            catch (Exception ex) when (ex is NpgsqlException or
                TimeoutException or InvalidOperationException or
                OperationCanceledException)
            {
                // Commit outcome may be unknown. Never retry automatically
                // or silently claim that nothing was persisted.
                state.Status = "InDoubt";
                return ConsoleChangeResult.Unavailable;
            }
        }
        finally { _gate.Release(); }
    }
}
