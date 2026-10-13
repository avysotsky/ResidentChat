using System.Security.Cryptography;
using System.Text;

namespace ResidentChat.Api.Operator;

/// <summary>
/// Disabled unless every security-critical setting is explicitly configured.
/// No default secret, tenant, database connection or enabled state.
/// </summary>
public sealed class OperatorOptions
{
    public bool Enabled { get; init; }
    public string? Issuer { get; init; }
    public string? Audience { get; init; }
    public string? JwtSigningKey { get; init; }
    public string? AuditHmacKey { get; init; }
    public string? ConnectionString { get; init; }
    public IReadOnlySet<Guid> AllowedTenantIds { get; init; } = new HashSet<Guid>();

    public bool Ready => Enabled
        && !string.IsNullOrWhiteSpace(Issuer)
        && !string.IsNullOrWhiteSpace(Audience)
        && ValidKey(JwtSigningKey)
        && ValidKey(AuditHmacKey)
        && !string.Equals(JwtSigningKey, AuditHmacKey, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(ConnectionString)
        && AllowedTenantIds.Count > 0
        && AllowedTenantIds.All(id => id != Guid.Empty);

    private static bool ValidKey(string? key) =>
        key is { Length: >= 32 and <= 512 } &&
        Encoding.UTF8.GetByteCount(key) >= 32;

    public string PseudonymizeActor(Guid tenantId, string subject)
    {
        if (!Ready || string.IsNullOrWhiteSpace(subject) || subject.Length > 128)
            throw new InvalidOperationException("Operator identity is not valid.");

        var key = Encoding.UTF8.GetBytes(AuditHmacKey!);
        var data = Encoding.UTF8.GetBytes(tenantId.ToString("N") + ":" + subject);
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(key, data));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public bool IsAllowedTenant(Guid tenantId) =>
        Ready && tenantId != Guid.Empty && AllowedTenantIds.Contains(tenantId);
}
