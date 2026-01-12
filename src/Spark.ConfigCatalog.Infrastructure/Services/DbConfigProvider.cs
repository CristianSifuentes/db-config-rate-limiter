using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Spark.ConfigCatalog.Domain;

namespace Spark.ConfigCatalog.Infrastructure.Services;

/// <summary>
/// DB-backed config provider with in-memory caching.
/// 
/// Design goals:
/// - Safe on the hot path (cache first)
/// - Supports scope overrides (User/Client/Tenant/Global precedence)
/// - Never throws for parse errors: returns null and emits telemetry
/// </summary>
public sealed class DbConfigProvider : IConfigProvider
{
    private static readonly JsonSerializerOptions JsonOpt = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<SparkConfigDbContext> _dbFactory;
    private readonly IMemoryCache _cache;
    private readonly TelemetryClient _telemetry;
    private readonly ILogger<DbConfigProvider> _log;

    public DbConfigProvider(
        IDbContextFactory<SparkConfigDbContext> dbFactory,
        IMemoryCache cache,
        TelemetryClient telemetry,
        ILogger<DbConfigProvider> log)
    {
        _dbFactory = dbFactory;
        _cache = cache;
        _telemetry = telemetry;
        _log = log;
    }

    public async Task<T?> GetAsync<T>(string conceptKey, string entryKey, ConfigScope scope, CancellationToken ct = default)
    {
        var cacheKey = CacheKey(conceptKey, entryKey, scope);

        if (_cache.TryGetValue(cacheKey, out T? cached))
            return cached;

        // Cache miss => hit DB once.
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var now = DateTime.UtcNow;
        var chain = BuildScopeChain(scope);

        // Pull only matching concept+entry, and filter validity; then pick by scope precedence.
        var candidates = await db.ConfigEntries
            .AsNoTracking()
            .Include(e => e.Concept)
            .Where(e => e.IsEnabled
                && e.Concept.IsEnabled
                && e.Concept.Key == conceptKey
                && e.Key == entryKey
                && (e.ValidFromUtc == null || e.ValidFromUtc <= now)
                && (e.ValidToUtc == null || e.ValidToUtc >= now))
            .OrderByDescending(e => e.UpdatedAtUtc)
            .ToListAsync(ct);

        var selected = SelectByScopePrecedence(candidates, chain);
        if (selected is null)
            return CacheNull<T>(cacheKey);

        var value = TryParse<T>(conceptKey, entryKey, selected.ValueType, selected.Value, scope);
        if (value is null)
            return CacheNull<T>(cacheKey);

        // Short TTL is often enough; for higher scale consider concept-level cache or rowversion-based invalidation.
        _cache.Set(cacheKey, value, TimeSpan.FromSeconds(30));
        return value;
    }

    private static string CacheKey(string conceptKey, string entryKey, ConfigScope scope)
        => $"cfg:{conceptKey}:{entryKey}:{scope.ScopeType}:{scope.ScopeKey ?? "null"}";

    private T? CacheNull<T>(string cacheKey)
    {
        // Negative caching avoids hammering DB when a key is missing.
        _cache.Set(cacheKey, default(T), TimeSpan.FromSeconds(10));
        return default;
    }

    private static IReadOnlyList<ConfigScope> BuildScopeChain(ConfigScope requested)
    {
        // Default precedence (customize to your identity model):
        // requested -> (if requested is User: Client?) -> (Tenant?) -> Global.
        // For this sample we keep it explicit and predictable:
        // - If caller asks for Tenant, we fall back to Global.
        // - If caller asks for Client, we fall back to Global.
        // - If caller asks for User, we fall back to Global.
        return new[] { requested, ConfigScope.Global() };
    }

    private static Infrastructure.Entities.ConfigEntry? SelectByScopePrecedence(
        IReadOnlyList<Infrastructure.Entities.ConfigEntry> candidates,
        IReadOnlyList<ConfigScope> chain)
    {
        foreach (var scope in chain)
        {
            var match = candidates.FirstOrDefault(e =>
                string.Equals(e.ScopeType, scope.ScopeType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.ScopeKey ?? string.Empty, scope.ScopeKey ?? string.Empty, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                return match;
        }

        return null;
    }

    private T? TryParse<T>(string conceptKey, string entryKey, string valueType, string raw, ConfigScope scope)
    {
        try
        {
            object? parsed = valueType.ToLowerInvariant() switch
            {
                "int" => int.Parse(raw),
                "double" => double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture),
                "bool" => bool.Parse(raw),
                "string" => raw,
                "json" or _ => JsonSerializer.Deserialize<T>(raw, JsonOpt)
            };

            return parsed is null ? default : (T?)parsed;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Config parse failed concept={Concept} entry={Entry} scope={ScopeType}:{ScopeKey} valueType={ValueType}",
                conceptKey, entryKey, scope.ScopeType, scope.ScopeKey, valueType);

            _telemetry.TrackTrace(
                "Config parse failed",
                SeverityLevel.Error,
                new Dictionary<string, string>
                {
                    ["concept"] = conceptKey,
                    ["entry"] = entryKey,
                    ["scopeType"] = scope.ScopeType,
                    ["scopeKey"] = scope.ScopeKey ?? string.Empty,
                    ["valueType"] = valueType
                });

            _telemetry.TrackException(ex);
            return default;
        }
    }
}
