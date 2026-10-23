using System.Security.Cryptography;
using System.Text;

namespace ResidentChat.Core.Ingress;

/// <summary>Fail-closed configuration; never expose the configured secret via diagnostics.</summary>
public sealed class TelegramIngressPolicy
{
    private readonly byte[] _secret;
    private readonly HashSet<long> _allowedChatIds;

    public bool Enabled { get; }
    public bool Ready { get; }

    public TelegramIngressPolicy(bool enabled, string? webhookSecret, IEnumerable<long>? allowedChatIds)
    {
        Enabled = enabled;
        _secret = webhookSecret is null ? [] : Encoding.ASCII.GetBytes(webhookSecret);
        _allowedChatIds = new HashSet<long>(allowedChatIds ?? []);
        Ready = enabled
            && webhookSecret is { Length: >= 32 and <= 256 }
            && webhookSecret.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-')
            && _allowedChatIds.Count > 0
            && _allowedChatIds.All(id => id < 0);
    }

    public bool VerifySecret(string? presented)
    {
        if (!Ready || presented is null || presented.Length != _secret.Length)
            return false;

        // Strict ASCII alphabet validated above; caller value is still untrusted.
        if (!presented.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-'))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(presented), _secret);
    }

    public bool IsAllowedGroup(long chatId) => Ready && _allowedChatIds.Contains(chatId);

    /// <summary>Require a complete trusted chat-ID-to-tenant mapping.</summary>
    public bool MatchesGroupSet(IEnumerable<long> configuredIds) =>
        Ready && _allowedChatIds.SetEquals(configuredIds);
}
