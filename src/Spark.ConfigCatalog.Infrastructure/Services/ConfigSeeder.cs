using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Spark.ConfigCatalog.Domain;
using Spark.ConfigCatalog.Infrastructure.Entities;

namespace Spark.ConfigCatalog.Infrastructure.Services;

public static class ConfigSeeder
{
    private static readonly JsonSerializerOptions JsonOpt = new(JsonSerializerDefaults.Web);

    public static async Task EnsureSeededAsync(SparkConfigDbContext db, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);

        // Concept: rate_limits
        var concept = await db.ConfigConcepts.FirstOrDefaultAsync(c => c.Key == "rate_limits", ct);
        if (concept is null)
        {
            concept = new ConfigConcept
            {
                Key = "rate_limits",
                Name = "Rate Limiting",
                Description = "Global + policy rate limiting knobs.",
                IsEnabled = true,
                UpdatedAtUtc = DateTime.UtcNow
            };
            db.ConfigConcepts.Add(concept);
            await db.SaveChangesAsync(ct);
        }

        async Task UpsertGlobalJson(string entryKey, object value)
        {
            var json = JsonSerializer.Serialize(value, JsonOpt);

            var e = await db.ConfigEntries.FirstOrDefaultAsync(x => x.ConceptId == concept.Id
                && x.Key == entryKey
                && x.ScopeType == "Global"
                && x.ScopeKey == null, ct);

            if (e is null)
            {
                db.ConfigEntries.Add(new ConfigEntry
                {
                    ConceptId = concept.Id,
                    Key = entryKey,
                    ValueType = "json",
                    Value = json,
                    ScopeType = "Global",
                    ScopeKey = null,
                    IsEnabled = true,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
            else
            {
                // Keep existing if user already customized; only seed if empty.
                if (string.IsNullOrWhiteSpace(e.Value))
                {
                    e.Value = json;
                    e.ValueType = "json";
                    e.IsEnabled = true;
                    e.UpdatedAtUtc = DateTime.UtcNow;
                }
            }
        }

        await UpsertGlobalJson("global", new RateLimitOptions { PerIdentityPerMinute = 300, BurstPer10Seconds = 50 });

        await UpsertGlobalJson("enterprise", new RateLimitingEnterpriseOptions
        {
            Exports = new RateLimitingEnterpriseOptions.EndpointLimits { PerTenantPerMinute = 600, PerClientPerMinute = 300, PerUserPerMinute = 120 },
            Search = new RateLimitingEnterpriseOptions.EndpointLimits { PerTenantPerMinute = 900, PerClientPerMinute = 600, PerUserPerMinute = 240 },
            Login = new RateLimitingEnterpriseOptions.LoginLimits { PerIpPerMinute = 30, PerClientPerMinute = 60 }
        });

        await db.SaveChangesAsync(ct);
    }
}
