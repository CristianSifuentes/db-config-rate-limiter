using App.ConfigCatalog.Infrastructure.Entities;

namespace App.ConfigCatalog.Infrastructure.RateLimiting.Interfaces;

public interface IRateLimitAuditStore
{
    Task UpsertMinuteAggAsync(RateLimitMinuteAgg row, CancellationToken ct);

    Task AddViolationsAsync(IEnumerable<RateLimitViolation> rows, CancellationToken ct);

    Task PersistAsync(
        IEnumerable<RateLimitIdentity> identities,
        IEnumerable<RateLimitMinuteAgg> minuteAggs,
        IEnumerable<RateLimitViolation> violations,
        IEnumerable<RateLimitBlock> blocks,
        CancellationToken ct);
}
