using Microsoft.EntityFrameworkCore;

namespace App.ConfigCatalog.Infrastructure.RateLimiting.ReadModels;

// Keyless read-model: comes from a VIEW or SQL projection.
[Keyless]
public sealed class RateLimitMinuteAggRow
{
    // ---- Core aggregate (same semantics as RateLimitMinuteAgg table) ----
    public DateTime WindowStartUtc { get; set; }            // minute bucket (UTC, truncated)
    public string Policy { get; set; } = default!;          // e.g. "exports-tenant", "search-user", "global"
    public string IdentityKind { get; set; } = default!;    // Tenant|Client|User|Ip
    public byte[] IdentityHash { get; set; } = default!;    // SHA-256 (VARBINARY(32))

    public string? RouteTemplate { get; set; }              // optional (beware cardinality)
    public string? Method { get; set; }                     // GET/POST/...

    public long Requests { get; set; }
    public long Rejected { get; set; }                      // 429 counts

    public int? MaxObservedConcurrency { get; set; }        // optional
    public int? LastStatusCode { get; set; }                // optional

    // ---- Enrichment (optional, from RateLimitIdentity) ----
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? UserId { get; set; }
    public string? Ip { get; set; }
    public string? KeyPlain { get; set; }                   // only if allowed (masked / non-PII)
}
