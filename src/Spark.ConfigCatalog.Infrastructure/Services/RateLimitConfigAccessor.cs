using Microsoft.Extensions.Caching.Memory;
using Spark.ConfigCatalog.Domain;

namespace Spark.ConfigCatalog.Infrastructure.Services;

public sealed class RateLimitConfigAccessor : IRateLimitConfigAccessor
{
    private readonly IConfigProvider _cfg;
    private readonly IMemoryCache _cache;

    // Atomic swap targets (reads are lock-free).
    private volatile RateLimitOptions _global = new();
    private volatile RateLimitingEnterpriseOptions _enterpriseGlobal = new();

    public RateLimitOptions Global => _global;
    public RateLimitingEnterpriseOptions EnterpriseGlobal => _enterpriseGlobal;

    public RateLimitConfigAccessor(IConfigProvider cfg, IMemoryCache cache)
    {
        _cfg = cfg;
        _cache = cache;
    }

    public RateLimitingEnterpriseOptions GetEnterpriseForTenant(string tenantId)
        => _cache.GetOrCreate($"rl:tenant:{tenantId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return GetOrFallbackSync("rate_limits", "enterprise", ConfigScope.Tenant(tenantId), _enterpriseGlobal);
        })!;

    public RateLimitingEnterpriseOptions GetEnterpriseForClient(string clientId)
        => _cache.GetOrCreate($"rl:client:{clientId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return GetOrFallbackSync("rate_limits", "enterprise", ConfigScope.Client(clientId), _enterpriseGlobal);
        })!;

    public async Task RefreshAsync(CancellationToken ct)
    {
        var global = await _cfg.GetAsync<RateLimitOptions>("rate_limits", "global", ConfigScope.Global(), ct)
                     ?? new RateLimitOptions();

        var enterprise = await _cfg.GetAsync<RateLimitingEnterpriseOptions>("rate_limits", "enterprise", ConfigScope.Global(), ct)
                        ?? new RateLimitingEnterpriseOptions();

        _global = global;
        _enterpriseGlobal = enterprise;

        // Also clear any computed overrides so they refresh quickly.
        // (Optional: do selective invalidation if you include rowversion/change tracking.)
        // IMemoryCache has no native pattern delete; in prod you could use a keyed wrapper.
    }

    private RateLimitingEnterpriseOptions GetOrFallbackSync(string concept, string entry, ConfigScope scope, RateLimitingEnterpriseOptions fallback)
    {
        try
        {
            return _cfg.GetAsync<RateLimitingEnterpriseOptions>(concept, entry, scope)
                       .GetAwaiter().GetResult() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
