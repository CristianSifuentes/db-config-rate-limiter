using System.Security;
using System.Security.Claims;
using Microsoft.Identity.Web;

namespace App.ConfigCatalog.Api.Security;

/// <summary>
/// Request-scoped tenant snapshot for authorization/auditing.
/// Supports Entra "new" + legacy claim types (Microsoft.Identity.Web ClaimConstants).
/// </summary>
public sealed record TenantContext(
    string TenantId,
    string? ObjectId,
    string? ClientAppId,
    bool IsAppOnly,
    string? ScopesRaw,
    string[] Roles);

public static class TenantContextFactory
{
    public static TenantContext From(ClaimsPrincipal user)
    {
        if (user is null) throw new ArgumentNullException(nameof(user));

        // ---- Resolve core identity ----
        var tenantId = FirstValue(user, ClaimConstants.Tid, ClaimConstants.TenantId)
            ?? throw new SecurityException("Missing tenant id claim (tid/tenantid).");

        var objectId = FirstValue(user, ClaimConstants.Oid, ClaimConstants.ObjectId);

        // Client app id:
        // - azp is typical for delegated and app-only tokens
        // - appid is also common in some tokens / older patterns
        var clientAppId = FirstValue(user, "azp", "appid");

        // ---- Permissions (useful in auditing & auth decisions) ----
        var scopesRaw = FirstValue(user, ClaimConstants.Scp, ClaimConstants.Scope);
        var roles = AllValues(user, ClaimConstants.Roles, ClaimConstants.Role, ClaimTypes.Role);

        // Heurística estándar:
        // - delegated: has scp
        // - app-only: no scp + has roles
        var isAppOnly = string.IsNullOrWhiteSpace(scopesRaw) && roles.Length > 0;

        return new TenantContext(
            TenantId: tenantId,
            ObjectId: objectId,
            ClientAppId: clientAppId,
            IsAppOnly: isAppOnly,
            ScopesRaw: scopesRaw,
            Roles: roles);
    }

    // =========================
    // Helpers (private)
    // =========================

    private static string? FirstValue(ClaimsPrincipal p, params string[] types)
        => types.Select(t => p.FindFirst(t)?.Value)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string[] AllValues(ClaimsPrincipal p, params string[] types)
        => types.SelectMany(t => p.FindAll(t).Select(c => c.Value))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
}
