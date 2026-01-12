namespace Spark.ConfigCatalog.Domain;

/// <summary>
/// Global rate limiting knobs (hot path). Keep this small and fast.
/// </summary>
public sealed class RateLimitOptions
{
    public int PerIdentityPerMinute { get; init; } = 300;
    public int BurstPer10Seconds { get; init; } = 50;
}
