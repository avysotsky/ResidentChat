using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using ResidentChat.Core.Ingress;

namespace ResidentChat.Api.Ingress;

/// <summary>Explicit PC-only Telegram getUpdates mode. Not configured by default.</summary>
public sealed class TelegramPollingOptions
{
    private TelegramPollingOptions(bool enabled, bool ready, string token)
    {
        Enabled = enabled;
        Ready = ready;
        BotToken = token;
    }

    public bool Enabled { get; }
    public bool Ready { get; }
    internal string BotToken { get; }

    public static TelegramPollingOptions Create(
        IConfiguration config, DurableTelegramIntakeOptions intake)
    {
        var section = config.GetSection("ResidentChat:TelegramPolling");
        var enabled = section.GetValue<bool>("Enabled");
        // Transport is an explicit choice. Other configurations are unable
        // to start getUpdates even if a bot token is accidentally present.
        var isolatedTransport = string.Equals(section["Transport"],
            "getUpdates-only", StringComparison.Ordinal);
        var token = section["BotToken"] ?? "";
        var colon = token.IndexOf(':');
        var validToken = colon >= 6 && colon < 16
            && token.Length is >= 30 and <= 160
            && token.AsSpan(0, colon).IndexOfAnyExceptInRange('0', '9') < 0
            && token[(colon + 1)..].All(c =>
                char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
        return new TelegramPollingOptions(enabled,
            enabled && isolatedTransport && intake.Ready && validToken, token);
    }
}

/// <summary>
/// A single sequential Telegram getUpdates consumer; OFF by default.
/// No LAN listener or Telegram send/delete method. Telegram offsets may only
/// advance after complete encrypted-review/proposal PostgreSQL admission
/// for a fully allowlisted tenant-scoped GROUP MESSAGE. Unknown/service,
/// foreign or malformed events block the queue for explicit disposition.
/// A metadata-only intake is never enough to confirm live Telegram updates.
/// </summary>
/// <summary>
/// Pure fail-closed guard for Telegram offset advancement. The policy is
/// intentionally separate from HTTP transport to unit-test ACK safety.
/// False means STOP processing, not silently skip a pending Telegram update.
/// </summary>
public static class TelegramPollingAckGuard
{
    public static bool CanAdmit(
        TelegramParseKind kind, bool allowedGroup, bool mappedTenant,
        bool classifiedReady) =>
        kind == TelegramParseKind.GroupMessage && allowedGroup
        && mappedTenant && classifiedReady;
}

public sealed class TelegramLongPollingWorker(
    TelegramPollingOptions options,
    DurableTelegramIntakeOptions intake,
    TelegramIngressPolicy groupPolicy,
    ClassifiedTelegramOptions classified,
    ClassifiedTelegramPipeline pipeline) : BackgroundService
{
    private const int MaxResponseBytes = 262_144;
    private const int BatchSize = 30;
    private const int SecondsLongPoll = 15;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        if (!options.Ready)
            throw new InvalidOperationException(
                "Telegram long polling requires an exact durable database tenant mapping.");
        if (!classified.Enabled || !classified.Ready)
            throw new InvalidOperationException(
                "Encrypted review and local classification MUST be enabled; no metadata-only polling.");

        using var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        long offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(client, options.BotToken, stoppingToken,
                    offsetValue => offset = offsetValue, offset);
                continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException
                or IOException or InvalidOperationException
                or JsonException or TimeoutException or OperationCanceledException
                or Npgsql.NpgsqlException)
            {
                // Bot token is part of request URL. Intentionally never
                // print exception messages/URLs, updates or chat IDs.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(6), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(HttpClient client, string token,
        CancellationToken ct, Action<long> commitOffset, long offset)
    {
        // Fixed Telegram HTTPS destination; never derived from user messages.
        // getUpdates is the ONLY Telegram operation: no sendMessage or
        // deleteWebhook. Another consumer/webhook must be disabled manually.
        var target = new Uri("https://api.telegram.org/bot" + token
            + "/getUpdates?timeout=" + SecondsLongPoll
            + "&limit=" + BatchSize
            + "&offset=" + offset.ToString(CultureInfo.InvariantCulture));
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException("Telegram poll unavailable.");

        var buffer = new byte[MaxResponseBytes + 1];
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            int length = 0;
            while (length < buffer.Length)
            {
                var count = await input.ReadAsync(
                    buffer.AsMemory(length, buffer.Length - length), ct);
                if (count == 0) break;
                length += count;
            }
            if (length > MaxResponseBytes)
                throw new InvalidOperationException("Telegram batch exceeds size cap.");

            using var json = JsonDocument.Parse(
                buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 24 });
            var root = json.RootElement;
            if (!root.TryGetProperty("ok", out var ok)
                || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Array
                || result.GetArrayLength() > BatchSize)
                throw new InvalidOperationException("Telegram batch schema invalid.");

            foreach (var update in result.EnumerateArray())
            {
                if (update.ValueKind != JsonValueKind.Object
                    || !update.TryGetProperty("update_id", out var id)
                    || !id.TryGetInt64(out var telegramId)
                    || telegramId < 0 || telegramId == long.MaxValue)
                    throw new InvalidOperationException("Telegram update ID invalid.");

                if (telegramId < offset) continue;
                var parsed = TelegramUpdateInspector.Inspect(update);

                // Service events, unsupported kinds, unauthorized chats and
                // unconfigured group mappings require explicit operator
                // handling. NEVER increment the Telegram cursor for them.
                // This intentionally leaves the queue stalled, safely, until
                // a later durable-disposition policy is implemented.
                bool authorized = parsed.Kind == TelegramParseKind.GroupMessage
                    && groupPolicy.IsAllowedGroup(parsed.ChatId);
                bool mapped = authorized
                    && intake.TryResolveTenant(parsed.ChatId, out _);
                if (!TelegramPollingAckGuard.CanAdmit(parsed.Kind, authorized,
                    mapped, classified.Ready))
                    throw new InvalidOperationException(
                        "Unclassified Telegram update blocks offset advancement.");

                if (!intake.TryResolveTenant(parsed.ChatId, out var tenantId))
                    throw new InvalidOperationException("Telegram tenant mapping missing.");

                // AcceptAsync must atomically commit a pending proposal or
                // encrypted human-review item, or detect a durable duplicate,
                // before offset is moved. Failure must leave update pending.
                var message = ClassifiedTelegramPipeline.ExtractMessageText(update);
                await pipeline.AcceptAsync(
                    tenantId, telegramId, parsed.ChatId, message, ct);

                offset = telegramId + 1;
                commitOffset(offset);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
