using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ResidentChat.Core.Demo;
using ResidentChat.Core.Ingress;
using ResidentChat.Core.LocalAi;

namespace ResidentChat.Api.Ingress;

/// <summary>
/// Offline test-group-only classification gate. Disabled by default and
/// requires both the durable PostgreSQL ingress and durable proposal queue.
/// Does not permit any real resident-group activation.
/// </summary>
public sealed class ClassifiedTelegramOptions
{
    private ClassifiedTelegramOptions(bool enabled, bool ready, Uri? endpoint,
        string model)
    {
        Enabled = enabled;
        Ready = ready;
        Endpoint = endpoint;
        Model = model;
    }

    public bool Enabled { get; }
    public bool Ready { get; }
    public Uri? Endpoint { get; }
    public string Model { get; }

    public static ClassifiedTelegramOptions Create(IConfiguration config,
        DurableTelegramIntakeOptions intake, Operator.DurableProposalOptions proposals)
    {
        var section = config.GetSection("ResidentChat:ClassifiedTelegram");
        var enabled = section.GetValue<bool>("Enabled");
        var mode = string.Equals(section["Mode"],
            "maintenance-only-test-groups", StringComparison.Ordinal);
        Uri? endpoint = Uri.TryCreate(section["OllamaEndpoint"],
            UriKind.Absolute, out var parsed) ? parsed : null;
        var model = section["Model"] ?? "";
        var safeModel = model is { Length: >= 1 and <= 128 }
            && model.All(c => char.IsAsciiLetterOrDigit(c)
                || c is '_' or '-' or '.' or ':' or '/');
        return new ClassifiedTelegramOptions(enabled,
            enabled && mode && intake.Ready && proposals.Ready
            && LocalModelClassifier.IsApprovedEndpoint(endpoint) && safeModel,
            endpoint, model);
    }
}

/// <summary>
/// Single-flight local-Qwen admission. No HTTP ACK or Telegram offset advance
/// until classification AND intake+proposal metadata commit atomically.
/// The model has no tools and is not allowed to authorize ticket creation.
/// Unsupported/unclassified messages are NOT ACKed in this deliberately
/// restricted maintenance-only experiment: they can be retried.
/// </summary>
public sealed class ClassifiedTelegramPipeline(
    ClassifiedTelegramOptions options, DurableTelegramIntakeOptions intake,
    PostgresTelegramIntakeStore store)
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <summary>Extract only plain text in memory, never persist or log it.</summary>
    public static string? ExtractMessageText(JsonElement update)
    {
        if (update.ValueKind != JsonValueKind.Object
            || !update.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("text", out var text)
            || text.ValueKind != JsonValueKind.String)
            return null;
        var value = text.GetString();
        return value is { Length: > 0 }
            && !string.IsNullOrWhiteSpace(value)
            && value.Length <= LocalModelClassifier.MaxMessageChars
            ? value : null;
    }

    public async Task<bool> AcceptAsync(Guid tenantId, long updateId,
        long groupId, string? text, CancellationToken ct)
    {
        if (!options.Ready || options.Endpoint is null
            || !intake.TryResolveTenant(groupId, out var expected)
            || expected != tenantId)
            throw new InvalidOperationException("Classified Telegram test intake disabled.");
        if (string.IsNullOrWhiteSpace(text)
            || text.Length > LocalModelClassifier.MaxMessageChars)
            throw new InvalidOperationException("Telegram message cannot be classified.");

        // Bound Ollama load to one concurrent request on this application
        // instance so it will not unexpectedly compete with TradeOps-v2.
        await _oneAtATime.WaitAsync(ct);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var model = LocalModelClassifier.CreateLoopback(
                options.Endpoint, options.Model);
            var decision = await model.ClassifyAsync(text, deadline.Token);
            var plan = DemoDispatchPolicy.Plan(tenantId, decision);
            if (plan.Queue != DemoQueueKind.TicketProposal
                || plan.Proposal is not { RequiresHumanApproval: true } proposal)
            {
                // FAQ, spam, emergency, ambiguity, unsafe model or timeout
                // cannot be safely reduced to a maintenance ticket. Do not
                // commit or acknowledge them until a review queue exists.
                throw new InvalidOperationException(
                    "Classification not eligible for maintenance proposal.");
            }

            return await store.AcceptWithProposalAsync(tenantId, updateId,
                groupId, proposal, ct);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}
