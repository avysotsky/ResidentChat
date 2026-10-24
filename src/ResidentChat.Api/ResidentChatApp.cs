using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ResidentChat.Core.Ingress;
using ResidentChat.Api.Ingress;
using Npgsql;
using ResidentChat.Api.Operator;

namespace ResidentChat.Api;

public static class ResidentChatApp
{
    public const int MaxWebhookBytes = 65_536;
    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    /// <summary>Accept configuration overrides for isolated synthetic integration tests.</summary>
    public static WebApplication Build(
        string[] args,
        IDictionary<string, string?>? configurationOverrides = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (configurationOverrides is not null)
            builder.Configuration.AddInMemoryCollection(configurationOverrides);

        // Defense in depth: Kestrel rejects excessive bodies even when Content-Length is absent.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = MaxWebhookBytes;
        });

        var policy = new TelegramIngressPolicy(
            builder.Configuration.GetValue<bool>("ResidentChat:Telegram:Enabled"),
            builder.Configuration["ResidentChat:Telegram:WebhookSecret"],
            builder.Configuration
                .GetSection("ResidentChat:Telegram:AllowedChatIds")
                .Get<long[]>() ?? []);

        builder.Services.AddSingleton(policy);
        builder.Services.AddSingleton<BoundedUpdateDeduplicator>();

        // No external AI providers or message-forwarding dependencies are registered.
        OperatorApi.Register(builder);
        SyntheticConsoleApi.Register(builder);

        // RC-10: never enabled implicitly. Durable intake is tenant-bound
        // to the validated operator PostgreSQL role/configuration.
        builder.Services.AddSingleton(sp => DurableTelegramIntakeOptions.Create(
            builder.Configuration, policy, sp.GetRequiredService<OperatorOptions>()));
        builder.Services.AddSingleton(sp => new PostgresTelegramIntakeStore(
            sp.GetRequiredService<OperatorOptions>().ConnectionString ?? "",
            sp.GetRequiredService<DurableTelegramIntakeOptions>()));

        // The PC does not expose a public webhook when optional long polling
        // is chosen. Both live intake and polling stay DISABLED by default.
        builder.Services.AddSingleton(sp => TelegramPollingOptions.Create(
            builder.Configuration, sp.GetRequiredService<DurableTelegramIntakeOptions>()));
        builder.Services.AddHostedService<TelegramLongPollingWorker>();

        builder.Services.AddSingleton(sp => DurableProposalOptions.Create(
            builder.Configuration, sp.GetRequiredService<OperatorOptions>(),
            sp.GetRequiredService<DurableTelegramIntakeOptions>()));
        builder.Services.AddSingleton(sp => new PostgresDurableProposalStore(
            sp.GetRequiredService<OperatorOptions>().ConnectionString ?? "",
            sp.GetRequiredService<DurableProposalOptions>()));
        builder.Services.AddSingleton(sp => ClassifiedTelegramOptions.Create(
            builder.Configuration,
            sp.GetRequiredService<DurableTelegramIntakeOptions>(),
            sp.GetRequiredService<DurableProposalOptions>(),
            sp.GetRequiredService<TelegramReviewOptions>()));
        builder.Services.AddSingleton(sp => TelegramReviewOptions.Create(
            builder.Configuration, sp.GetRequiredService<DurableTelegramIntakeOptions>()));
        builder.Services.AddSingleton(sp => new PostgresTelegramReviewStore(
            sp.GetRequiredService<OperatorOptions>().ConnectionString ?? "",
            sp.GetRequiredService<TelegramReviewOptions>(),
            sp.GetRequiredService<DurableTelegramIntakeOptions>(),
            sp.GetRequiredService<PostgresTelegramIntakeStore>()));
        builder.Services.AddSingleton<ClassifiedTelegramPipeline>();
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);

        var app = builder.Build();
        // RC-27: permanent operator UI is read-only at the HTTP boundary,
        // including direct ticket POST, synthetic webhook and all other
        // methods capable of mutation. No metadata-only Telegram admission.
        if (builder.Configuration.GetValue<bool>("ResidentChat:PersistentReadOnly:Enabled"))
        {
            app.Use(async (ctx, next) =>
            {
                if (!HttpMethods.IsGet(ctx.Request.Method)
                    && !HttpMethods.IsHead(ctx.Request.Method))
                {
                    ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    return;
                }
                await next(ctx);
            });
        }
        app.UseAuthentication();
        app.UseAuthorization();
        OperatorApi.Map(app);
        SyntheticConsoleApi.Map(app);
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        // Explicit experimental local-classification mode is OFF by default.
        // No external LLM or unauthenticated ticket creation is permitted.
        app.MapPost("/telegram/webhook", async (HttpContext context,
            TelegramIngressPolicy ingress, BoundedUpdateDeduplicator dedup,
            DurableTelegramIntakeOptions intake, PostgresTelegramIntakeStore store,
            TelegramPollingOptions polling,
            ClassifiedTelegramOptions classified,
            ClassifiedTelegramPipeline pipeline) =>
        {
            // Never accept a webhook in a process configured for getUpdates,
            // including incomplete polling config; fail closed to 404.
            if (polling.Enabled) return Results.NotFound();
            if (!ingress.Enabled)
                return Results.NotFound();

            if (!ingress.Ready)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            // Explicit opt-in with incomplete tenant, database or HMAC config
            // must not silently fall back to the volatile demo deduplicator.
            if (intake.Enabled && !intake.Ready)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (classified.Enabled && !classified.Ready)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            if (!context.Request.Headers.TryGetValue(SecretHeader, out var values)
                || values.Count != 1
                || !ingress.VerifySecret(values[0]))
                return Results.Unauthorized();

            if (context.Request.ContentLength > MaxWebhookBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            if (context.Request.ContentType is null
                || !context.Request.ContentType.StartsWith(
                    "application/json", StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

            byte[]? body;
            try
            {
                body = await ReadBoundedAsync(context.Request.Body, context.RequestAborted);
            }
            catch (BadHttpRequestException ex)
                when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            if (body is null)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            TelegramUpdateResult parsed;
            string? text = null;
            try
            {
                using JsonDocument json = JsonDocument.Parse(body,
                    new JsonDocumentOptions { MaxDepth = 16 });
                parsed = TelegramUpdateInspector.Inspect(json.RootElement);
                if (classified.Enabled && parsed.Kind == TelegramParseKind.GroupMessage)
                    text = ClassifiedTelegramPipeline.ExtractMessageText(json.RootElement);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }
            finally
            {
                Array.Clear(body); // Best-effort reduction of transient plaintext lifetime.
            }

            if (parsed.Kind == TelegramParseKind.Invalid)
                return Results.BadRequest();

            // Unsupported events and unauthorized chats are safely acknowledged to avoid retries.
            if (parsed.Kind != TelegramParseKind.GroupMessage
                || !ingress.IsAllowedGroup(parsed.ChatId))
                return Results.Ok(new { status = "ignored" });

            if (intake.Enabled)
            {
                if (!intake.TryResolveTenant(parsed.ChatId, out var tenantId))
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

                try
                {
                    // Mode A: synthetic metadata-only (existing behavior).
                    // Mode B: real local Qwen classification followed by
                    // ATOMIC intake+proposal commit before acknowledging.
                    var inserted = classified.Enabled
                        ? await pipeline.AcceptAsync(
                            tenantId, parsed.UpdateId, parsed.ChatId,
                            text, context.RequestAborted)
                        : await store.AcceptAsync(
                            tenantId, parsed.UpdateId, parsed.ChatId,
                            context.RequestAborted);
                    return inserted
                        ? Results.Ok(new { status = "accepted" })
                        : Results.Ok(new { status = "duplicate" });
                }
                catch (Exception ex) when (ex is NpgsqlException or IOException
                    or TimeoutException or InvalidOperationException
                    or OperationCanceledException)
                {
                    // Never log input, database errors, tokens or private chat IDs.
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            }

            return dedup.TryAccept(parsed.UpdateId)
                ? Results.Ok(new { status = "accepted" })
                : Results.Ok(new { status = "duplicate" });
        });

        return app;
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream input, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8_192];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > MaxWebhookBytes)
            {
                Array.Clear(buffer);
                return null;
            }
            output.Write(buffer, 0, count);
        }
        Array.Clear(buffer);
        return output.ToArray();
    }
}
