using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ResidentChat.Api;
using ResidentChat.Core.Ingress;

const string Secret = "SyntheticOnlyWebhookSecret_0123456789ABCDEF";
const long AllowedChat = -1001234567890;
const long ForeignChat = -1009876543210;

try
{
    var policy = new TelegramIngressPolicy(true, Secret, [AllowedChat]);
    Check(policy.Ready, "valid configuration ready");
    Check(policy.VerifySecret(Secret), "valid secret");
    Check(!policy.VerifySecret(Secret + "x"), "wrong secret");
    Check(!policy.VerifySecret(null), "missing secret");
    Check(!new TelegramIngressPolicy(true, "short", [AllowedChat]).Ready, "weak token denied");
    Check(!new TelegramIngressPolicy(true, Secret, []).Ready, "empty allowlist denied");
    Check(!new TelegramIngressPolicy(true, Secret, [12345]).Ready, "positive chat IDs denied");

    using (var doc = JsonDocument.Parse(GroupMessage(10, AllowedChat)))
    {
        var parsed = TelegramUpdateInspector.Inspect(doc.RootElement);
        Check(parsed.Kind == TelegramParseKind.GroupMessage && parsed.ChatId == AllowedChat,
            "synthetic group admission");
    }
    using (var doc = JsonDocument.Parse("{\"update_id\":17,\"message\":{\"chat\":{\"id\":123,\"type\":\"private\"}}}"))
        Check(TelegramUpdateInspector.Inspect(doc.RootElement).Kind == TelegramParseKind.Unsupported,
            "private messages ignored");

    var dedup = new BoundedUpdateDeduplicator(2);
    Check(dedup.TryAccept(1) && !dedup.TryAccept(1), "duplicate rejected");
    Check(dedup.TryAccept(2) && dedup.TryAccept(3), "bounded cache accepts newer updates");
    Check(dedup.TryAccept(1), "evicted old update can reappear: NOT durable idempotency");

    await using (var disabled = await StartAsync(false, false))
    {
        var response = await PostAsync(disabled.Client, GroupMessage(100, AllowedChat), Secret);
        Check(response.Status == HttpStatusCode.NotFound, "webhook disabled by default");
    }

    await using (var invalid = await StartAsync(true, false))
    {
        var response = await PostAsync(invalid.Client, GroupMessage(101, AllowedChat), Secret);
        Check(response.Status == HttpStatusCode.ServiceUnavailable, "enabled but unconfigured fails closed");
    }

    await using (var enabled = await StartAsync(true, true))
    {
        var client = enabled.Client;
        var valid = GroupMessage(200, AllowedChat);

        Check((await PostAsync(client, valid, null)).Status == HttpStatusCode.Unauthorized,
            "missing secret refused");
        Check((await PostAsync(client, valid, "WrongSyntheticToken_0123456789ABCDEF")).Status
            == HttpStatusCode.Unauthorized, "wrong secret refused");

        Check((await PostAsync(client, "not json", Secret)).Status == HttpStatusCode.BadRequest,
            "malformed JSON refused");
        Check((await PostAsync(client, "{\"message\":{}}", Secret)).Status == HttpStatusCode.BadRequest,
            "missing update_id refused");
        Check((await PostAsync(client, "{\"update_id\":-1}", Secret)).Status == HttpStatusCode.BadRequest,
            "negative update_id refused");
        Check((await PostAsync(client,
            "{\"update_id\":201,\"message\":{\"chat\":{\"id\":123,\"type\":\"private\"}}}", Secret)).Body
            .Contains("ignored", StringComparison.Ordinal), "private chat ignored");

        var foreign = await PostAsync(client, GroupMessage(202, ForeignChat), Secret);
        Check(foreign.Status == HttpStatusCode.OK && foreign.Body.Contains("ignored"),
            "foreign group ignored");

        var accepted = await PostAsync(client, valid, Secret);
        var duplicate = await PostAsync(client, valid, Secret);
        Check(accepted.Status == HttpStatusCode.OK && accepted.Body.Contains("accepted"),
            "allowed group admitted");
        Check(duplicate.Status == HttpStatusCode.OK && duplicate.Body.Contains("duplicate"),
            "HTTP duplicate detected");

        var excessive = await PostAsync(client, GroupMessage(203, AllowedChat,
            new string('x', ResidentChatApp.MaxWebhookBytes)), Secret);
        Check(excessive.Status == HttpStatusCode.RequestEntityTooLarge, "64 KiB size limit");

        var notJson = await PostAsync(client, valid, Secret, "text/plain");
        Check(notJson.Status == HttpStatusCode.UnsupportedMediaType, "content type restricted");

        Check(!accepted.Body.Contains("synthetic-resident-message", StringComparison.Ordinal),
            "response cannot echo chat content");
    }

    Console.WriteLine("RC-01 PASS: synthetic policy + loopback HTTP admission/security checks.");
    return 0;
}
catch (Exception ex)
{
    // Never print HTTP payloads or secrets. Test errors use fixed synthetic check labels.
    Console.Error.WriteLine("RC-01 FAIL: " + ex.GetType().Name);
    return 1;
}

static string GroupMessage(long updateId, long chatId, string text = "synthetic-resident-message") =>
    JsonSerializer.Serialize(new {
        update_id = updateId,
        message = new { chat = new { id = chatId, type = "supergroup" }, text }
    });

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
}

static async Task<(HttpStatusCode Status, string Body)> PostAsync(
    HttpClient client, string json, string? secret, string mediaType = "application/json")
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
    request.Content = new StringContent(json, Encoding.UTF8);
    request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
    // Send headers first for oversized payloads: Kestrel can reject with 413 without
    // tearing down a connection while the client is still uploading the body.
    if (Encoding.UTF8.GetByteCount(json) > ResidentChatApp.MaxWebhookBytes)
        request.Headers.ExpectContinue = true;
    if (secret is not null)
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", secret);
    using var response = await client.SendAsync(request);
    return (response.StatusCode, await response.Content.ReadAsStringAsync());
}

static async Task<LocalTestServer> StartAsync(bool enabled, bool configured)
{
    var cfg = new Dictionary<string, string?>
    {
        ["ResidentChat:Telegram:Enabled"] = enabled ? "true" : "false",
        ["ResidentChat:Telegram:WebhookSecret"] = configured ? Secret : "",
        ["ResidentChat:Telegram:AllowedChatIds:0"] = AllowedChat.ToString()
    };
    var app = ResidentChatApp.Build([], cfg);
    app.Urls.Add("http://127.0.0.1:0");
    await app.StartAsync();
    var server = app.Services.GetRequiredService<IServer>();
    var addresses = server.Features.Get<IServerAddressesFeature>()
        ?? throw new InvalidOperationException("Missing loopback listener");
    var url = addresses.Addresses.Single(a => a.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
    return new LocalTestServer(app, new HttpClient { BaseAddress = new Uri(url) });
}

internal sealed class LocalTestServer(WebApplication app, HttpClient client) : IAsyncDisposable
{
    public HttpClient Client { get; } = client;
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
