using System.Security.Claims;

namespace ResidentChat.Api.Operator;

/// <summary>
/// Scope always derives from a validated signed JWT, never from route/query/body/header.
/// </summary>
public static class OperatorAccess
{
    public static bool TryGetScope(ClaimsPrincipal principal,
        OperatorOptions options, out Guid tenantId, out string actorHash)
    {
        tenantId = Guid.Empty;
        actorHash = string.Empty;
        if (!principal.Identity?.IsAuthenticated ?? true)
            return false;

        var tenantClaim = principal.FindAll("tenant_id").ToArray();
        var subjectClaim = principal.FindAll("sub").ToArray();
        var roleClaim = principal.FindAll("role").ToArray();
        if (tenantClaim.Length != 1 || subjectClaim.Length != 1 || roleClaim.Length != 1
            || !Guid.TryParse(tenantClaim[0].Value, out tenantId)
            || !options.IsAllowedTenant(tenantId)
            || roleClaim[0].Value is not ("operator" or "supervisor")
            || string.IsNullOrWhiteSpace(subjectClaim[0].Value)
            || subjectClaim[0].Value.Length > 128)
            return false;

        actorHash = options.PseudonymizeActor(tenantId, subjectClaim[0].Value);
        return true;
    }
}
