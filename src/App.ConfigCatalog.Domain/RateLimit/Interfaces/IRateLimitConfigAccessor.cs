namespace App.ConfigCatalog.Domain;

/// <summary>
/// Hot-path accessor used by rate limiting policies.
/// Backed by a periodically refreshed cache (no SQL on every request).
/// </summary>
public interface IRateLimitConfigAccessor
{
    RateLimitOptions Global { get; }
    RateLimitingEnterpriseOptions EnterpriseGlobal { get; }

    RateLimitingEnterpriseOptions GetEnterpriseForTenant(string tenantId);
    RateLimitingEnterpriseOptions GetEnterpriseForClient(string clientId);
}
