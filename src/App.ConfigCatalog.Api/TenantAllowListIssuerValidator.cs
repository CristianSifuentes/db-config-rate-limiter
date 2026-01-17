using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace App.ConfigCatalog.Api
{
    /// <summary>
    /// Validates token issuer for multi-tenant scenarios using a tenant allow-list.
    ///
    /// Why:
    /// - In Entra ID multi-tenant, issuer changes per tenant:
    ///   https://login.microsoftonline.com/{tid}/v2.0
    /// - Setting ValidateIssuer=false is convenient but dangerous (any tenant can call you).
    /// - This validator enforces: token.tid must be in AllowedTenants unless AllowAnyTenant=true.
    /// </summary>
    internal static class TenantAllowListIssuerValidator
    {
        //public static Func<string, SecurityToken, TokenValidationParameters, string> Build(IConfiguration config)
        public static IssuerValidator Build(IConfiguration config)

        {
            var allowAny = config.GetValue<bool>("MultiTenant:AllowAnyTenant");

            var allowed = (config.GetSection("MultiTenant:AllowedTenants").Get<string[]>() ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return (issuer, token, parameters) =>
            {
                var tid = TryGetTenantId(token);
                if (string.IsNullOrWhiteSpace(tid))
                    throw new SecurityTokenInvalidIssuerException("Missing tenant id (tid) claim.");

                if (!allowAny && !allowed.Contains(tid))
                    throw new SecurityTokenInvalidIssuerException($"Tenant '{tid}' is not allowed.");

                // Optional issuer sanity check: issuer should contain tid
                // (We don't hardcode cloud hosts because national clouds exist).
                if (!string.IsNullOrWhiteSpace(issuer) && !issuer.Contains(tid, StringComparison.OrdinalIgnoreCase))
                    throw new SecurityTokenInvalidIssuerException("Issuer does not match tenant id.");

                return issuer;
            };
        }

        private static string? TryGetTenantId(SecurityToken token)
        {
            if (token is JsonWebToken jwt2)
                return jwt2.Claims.FirstOrDefault(c => c.Type == "tid")?.Value;

            if (token is JwtSecurityToken jwt)
                return jwt.Claims.FirstOrDefault(c => c.Type == "tid")?.Value;

            return null;
        }
    }

}
