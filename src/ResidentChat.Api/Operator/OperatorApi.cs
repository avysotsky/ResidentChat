using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ResidentChat.Core.Tickets;

namespace ResidentChat.Api.Operator;

public static class OperatorApi
{
    private const int MaxCommandBytes = 2_048;

    public static void Register(WebApplicationBuilder builder)
    {
        // No operator endpoint is exposed until the entire local configuration is valid.
        var config = builder.Configuration.GetSection("ResidentChat:Operator");
        var tenants = new HashSet<Guid>();
        var rawTenants = config.GetSection("AllowedTenantIds").Get<string[]>() ?? [];
        foreach (var id in rawTenants)
            if (Guid.TryParse(id, out var parsed) && parsed != Guid.Empty)
                tenants.Add(parsed);
        var options = new OperatorOptions
        {
            Enabled = config.GetValue<bool>("Enabled"),
            Issuer = config["Issuer"],
            Audience = config["Audience"],
            JwtSigningKey = config["JwtSigningKey"],
            AuditHmacKey = config["AuditHmacKey"],
            ConnectionString = config["ConnectionString"],
            // Fail closed if even one configured tenant is invalid or duplicated.
            AllowedTenantIds = tenants.Count == rawTenants.Length
                ? tenants : new HashSet<Guid>()
        };
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new PostgresTicketStore(options.ConnectionString ?? ""));

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(auth =>
            {
                auth.MapInboundClaims = false;
                auth.RequireHttpsMetadata = true;
                // Dummy key is never a usable configuration: operator routes stay disabled
                // unless Ready. Required only to avoid breaking RC-01 on empty config.
                var signingKey = options.Ready
                    ? options.JwtSigningKey! : new string('Z', 48);
                auth.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Ready ? options.Issuer : "disabled",
                    ValidateAudience = true,
                    ValidAudience = options.Ready ? options.Audience : "disabled",
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(signingKey)),
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = "role"
                };
            });

        builder.Services.AddAuthorization();
    }

    public static void Map(WebApplication app)
    {
        var options = app.Services.GetRequiredService<OperatorOptions>();
        if (!options.Ready)
            return; // 404 for all operator routes, including incomplete configuration.

        var group = app.MapGroup("/operator/v1/tickets")
            .RequireAuthorization();

        group.MapPost("", async (HttpContext ctx, PostgresTicketStore store,
            OperatorOptions settings) =>
        {
            if (!OperatorAccess.TryGetScope(ctx.User, settings, out var tenant, out var actor))
                return Results.Forbid();

            var (body, bad) = await ReadJsonAsync(ctx);
            if (bad is not null) return bad;

            using (body!)
            {
                var root = body.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || root.EnumerateObject().Count() != 2
                    || !root.TryGetProperty("category", out var categoryToken)
                    || !root.TryGetProperty("priority", out var priorityToken)
                    || categoryToken.ValueKind != JsonValueKind.String
                    || priorityToken.ValueKind != JsonValueKind.String
                    || !Enum.TryParse<TicketCategory>(categoryToken.GetString(), false, out var category)
                    || !Enum.TryParse<TicketPriority>(priorityToken.GetString(), false, out var priority)
                    || !TicketRules.IsCategory(category) || !TicketRules.IsPriority(priority))
                    return Results.BadRequest();

                var ticket = await store.CreateAsync(tenant, category, priority,
                    actor, ctx.RequestAborted);
                return Results.Created("/operator/v1/tickets/" + ticket.TicketId, ticket);
            }
        });

        group.MapGet("/{ticketId:guid}", async (Guid ticketId, HttpContext ctx,
            PostgresTicketStore store, OperatorOptions settings) =>
        {
            if (!OperatorAccess.TryGetScope(ctx.User, settings, out var tenant, out _))
                return Results.Forbid();

            var ticket = await store.GetAsync(tenant, ticketId, ctx.RequestAborted);
            return ticket is null ? Results.NotFound() : Results.Ok(ticket);
        });

        group.MapPost("/{ticketId:guid}/escalate", (Guid ticketId, HttpContext ctx,
            PostgresTicketStore store, OperatorOptions settings) =>
            Transition(ticketId, TicketStatus.Escalated, false, ctx, store, settings));

        group.MapPost("/{ticketId:guid}/resolve", (Guid ticketId, HttpContext ctx,
            PostgresTicketStore store, OperatorOptions settings) =>
            Transition(ticketId, TicketStatus.Resolved, true, ctx, store, settings));
    }

    private static async Task<IResult> Transition(Guid id, TicketStatus target,
        bool supervisorOnly, HttpContext ctx, PostgresTicketStore store,
        OperatorOptions options)
    {
        if (!OperatorAccess.TryGetScope(ctx.User, options, out var tenant, out var actor))
            return Results.Forbid();
        bool supervisor = ctx.User.HasClaim("role", "supervisor");
        if (supervisorOnly && !supervisor)
            return Results.Forbid();

        var (body, bad) = await ReadJsonAsync(ctx);
        if (bad is not null) return bad;

        using (body!)
        {
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("expectedVersion", out var versionToken)
                || versionToken.ValueKind != JsonValueKind.Number
                || !versionToken.TryGetInt32(out int version) || version < 1)
                return Results.BadRequest();

            var result = await store.TransitionAsync(tenant, id, version, target,
                supervisor, actor, ctx.RequestAborted);
            return result.Outcome switch
            {
                TicketMutationOutcome.Updated => Results.Ok(result.Ticket),
                TicketMutationOutcome.NotFound => Results.NotFound(),
                _ => Results.Conflict()
            };
        }
    }

    private static async Task<(JsonDocument? Json, IResult? Error)> ReadJsonAsync(
        HttpContext ctx)
    {
        var request = ctx.Request;
        if (request.ContentType is null || !request.ContentType.StartsWith(
                "application/json", StringComparison.OrdinalIgnoreCase))
            return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        if (request.ContentLength > MaxCommandBytes)
            return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));

        // Deliberately avoids model binding, raw-request logging and unknown-field passthrough.
        var buffer = new byte[MaxCommandBytes + 1];
        int n = 0;
        try
        {
            while (n < buffer.Length)
            {
                var next = await request.Body.ReadAsync(
                    buffer.AsMemory(n, buffer.Length - n), ctx.RequestAborted);
                if (next == 0) break;
                n += next;
            }

            if (n > MaxCommandBytes)
                return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));

            try
            {
                return (JsonDocument.Parse(buffer.AsMemory(0, n),
                    new JsonDocumentOptions { MaxDepth = 8 }), null);
            }
            catch (JsonException)
            {
                return (null, Results.BadRequest());
            }
        }
        catch (BadHttpRequestException ex)
            when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
