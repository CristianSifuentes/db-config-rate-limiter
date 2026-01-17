using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace App.ConfigCatalog.Infrastructure.RateLimiting.ReadModels;

// Keyless read-model: comes from a VIEW or a SQL projection.
[Keyless]
public sealed class RateLimitViolationRow
{
    // ---- Core violation (same semantics as RateLimitViolation table) ----
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }

    public string Policy { get; set; } = default!;          // e.g. "exports-tenant", "global"
    public string IdentityKind { get; set; } = default!;    // Tenant|Client|User|Ip
    public byte[] IdentityHash { get; set; } = default!;    // SHA-256 (VARBINARY(32))

    public string? TraceId { get; set; }
    public string? CorrelationId { get; set; }

    public string? Path { get; set; }
    public string? Method { get; set; }
    public int StatusCode { get; set; }                     // typically 429
    public int? RetryAfterSeconds { get; set; }
    public string? Reason { get; set; }                     // e.g. "exports_tenant_limit"

    // ---- Enrichment (optional, from RateLimitIdentity) ----
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? UserId { get; set; }
    public string? Ip { get; set; }
    public string? KeyPlain { get; set; }                   // only if you allow it (masked / non-PII)
}
