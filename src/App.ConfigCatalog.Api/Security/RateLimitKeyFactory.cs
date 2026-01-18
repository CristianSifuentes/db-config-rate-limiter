using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Identity.Web;

namespace App.ConfigCatalog.Api.Security
{
    /// <summary>
    /// Rate limiting key factory.
    /// Supports Entra "new" + legacy claim types (Microsoft.Identity.Web ClaimConstants).
    /// Produces stable partition keys like:
    /// - tenant:{tid}
    /// - client:{azp/appid}
    /// - user:{oid/sub/nameidentifier}
    /// - ip:{address}
    /// </summary>
    public static class RateLimitKeyFactory
    {
        public static string GetTenantKey(HttpContext ctx)
        {
            var tenantId = FirstValue(ctx.User, ClaimConstants.Tid, ClaimConstants.TenantId);
            return !string.IsNullOrWhiteSpace(tenantId)
                ? $"tenant:{tenantId}"
                : "tenant:anonymous";
        }

        public static string GetClientKey(HttpContext ctx)
        {
            // Entra commonly uses azp for delegated apps and appid for some flows.
            var clientAppId = FirstValue(ctx.User, "azp", "appid");
            return !string.IsNullOrWhiteSpace(clientAppId)
                ? $"client:{clientAppId}"
                : "client:anonymous";
        }

        public static string GetUserKey(HttpContext ctx)
        {
            // Prefer OID (best stable user object id),
            // fall back to legacy objectidentifier, then sub, then nameidentifier.
            var userId = FirstValue(
                ctx.User,
                ClaimConstants.Oid,
                ClaimConstants.ObjectId,
                ClaimConstants.Sub,
                ClaimConstants.NameIdentifierId,
                ClaimTypes.NameIdentifier);

            return !string.IsNullOrWhiteSpace(userId)
                ? $"user:{userId}"
                : "user:anonymous";
        }

        public static string GetIpFallback(HttpContext ctx)
        {
            // If you use ForwardedHeaders middleware correctly, RemoteIpAddress will be the real client IP.
            // Still, we can best-effort parse X-Forwarded-For for environments that don't map it.
            var ip =
                TryGetForwardedFor(ctx) ??
                ctx.Connection.RemoteIpAddress?.ToString();

            return !string.IsNullOrWhiteSpace(ip)
                ? $"ip:{ip}"
                : "ip:unknown";
        }

        // -----------------------
        // Helpers (private)
        // -----------------------

        private static string? FirstValue(ClaimsPrincipal user, params string[] claimTypes)
        {
            foreach (var t in claimTypes)
            {
                var v = user.FindFirst(t)?.Value;
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }

        private static string? TryGetForwardedFor(HttpContext ctx)
        {
            // Standard: X-Forwarded-For: client, proxy1, proxy2
            if (!ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var raw)) return null;

            var first = raw.ToString().Split(',')[0].Trim();
            if (IPAddress.TryParse(first, out _)) return first;

            return null;
        }
    }
}
