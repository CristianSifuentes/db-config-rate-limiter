using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace App.ConfigCatalog.Domain.Extensions.Configurations;

public static class Authorization
{
    public const string PublicPolicyName = "Public";

    public static void Configure(this WebApplicationBuilder builder)
    {
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(PublicPolicyName, policy =>
            {
                policy.RequireAuthenticatedUser();

                policy.RequireAssertion(ctx =>
                {
                    var scp = ctx.User.FindFirst("scp")?.Value ?? string.Empty;
                    var scopes = scp.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    var scopesOld = ctx.User.Claims
                        .Where(c => c.Type == "http://schemas.microsoft.com/identity/claims/scope")
                        .SelectMany(c => (c.Value ?? string.Empty)
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        .ToArray();

                    var roles = ctx.User.FindAll("roles").Select(r => r.Value).ToArray();

                    var allowedScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "Documents.Read"
                    };

                    var allowedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "Reports.Read.All"
                    };

                    return scopes.Any(allowedScopes.Contains)
                        || scopesOld.Any(allowedScopes.Contains)
                        || roles.Any(allowedRoles.Contains);
                });
            });
        });
    }
}
