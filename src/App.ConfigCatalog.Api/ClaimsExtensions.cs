using System.Security.Claims;

namespace App.ConfigCatalog.Api
{
    internal static class ClaimsExtensions
    {
        public static bool HasScope(this ClaimsPrincipal user, string scope)
        {
            var scp = user.FindFirstValue("scp");
            if (string.IsNullOrWhiteSpace(scp)) return false;

            return scp.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                      .Any(s => string.Equals(s, scope, StringComparison.OrdinalIgnoreCase));
        }

        public static bool HasAnyScope(this ClaimsPrincipal user, params string[] scopes)
            => scopes.Any(user.HasScope);

        public static bool HasAppRole(this ClaimsPrincipal user, string role)
        {
            return user.FindAll("roles")
                       .Any(r => string.Equals(r.Value, role, StringComparison.OrdinalIgnoreCase));
        }
    }

}
