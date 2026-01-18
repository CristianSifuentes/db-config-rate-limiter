using Microsoft.AspNetCore.Authorization;

namespace App.ConfigCatalog.Api
{

    /// <summary>
    /// Policy model:
    /// - Delegated access: "scp" claim contains OAuth scopes.
    /// - App-only access: "roles" claim contains app roles (application permissions).
    ///
    /// This supports real-world patterns:
    /// - Postman / user flows (delegated)
    /// - daemon services (client_credentials)
    /// </summary>
    internal static class AuthzPolicies
    {
        public const string DocumentsReadPolicyName = "Documents.Read";
        public const string ReportsReadPolicyName = "Reports.Read";
        public const string PublicPolicyName = "Public";

        public static void Configure(AuthorizationOptions options, IConfiguration config)
        {
            var docsScope = config["AuthZ:DocumentsReadScope"] ?? "Documents.Read";
            var reportsScope = config["AuthZ:ReportsReadAllScope"] ?? "Reports.Read.All";
            var reportsRole = config["AuthZ:ReportsReadAllAppRole"] ?? "Reports.Read.All";

            options.AddPolicy(DocumentsReadPolicyName, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(ctx => ctx.User.HasScope(docsScope));
            });

            // Reports can be accessed either by:
            // - delegated scope (scp) OR
            // - app-only role (roles)
            options.AddPolicy(ReportsReadPolicyName, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(ctx =>
                    ctx.User.HasScope(reportsScope) || ctx.User.HasAppRole(reportsRole));
                    
            });
        }
    }

}
