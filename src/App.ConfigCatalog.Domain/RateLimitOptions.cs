namespace App.ConfigCatalog.Domain;

/// <summary>
/// Global rate limiting knobs (hot path). Keep this small and fast.
/// </summary>
public sealed class RateLimitOptions
{
    public int PerIdentityPerMinute { get; init; } = 300;
    public int BurstPer10Seconds { get; init; } = 50;
}


public sealed record RateLimitAuditEvent(
    DateTimeOffset AtUtc,
    string Policy,
    string IdentityKind,
    string IdentityKey,      // tenant:xxx | client:yyy | user:zzz | ip:...
    string Method,
    string Path,
    int StatusCode,
    string? TraceId,
    string? CorrelationId,
    bool Rejected,
    int? RetryAfterSeconds,
    string? Reason);
