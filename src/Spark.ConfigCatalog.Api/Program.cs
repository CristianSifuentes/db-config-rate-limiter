using System.Threading.RateLimiting;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.EntityFrameworkCore;
using Spark.ConfigCatalog.Api;
using Spark.ConfigCatalog.Domain;
using Spark.ConfigCatalog.Infrastructure;
using Spark.ConfigCatalog.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Application Insights (optional): enabled when APPLICATIONINSIGHTS_CONNECTION_STRING is set.
builder.Services.AddApplicationInsightsTelemetry();

builder.Services.AddMemoryCache();

// --- DB ---
// For the demo we use SQLite by default (easy local run) with optional SQL Server.
// In SPARK, point this to your existing SPARKDbContext and add ConfigConcepts/ConfigEntries DbSets.
var conn = builder.Configuration.GetConnectionString("ConfigCatalog")
          ?? builder.Configuration.GetConnectionString("Sql")
          ?? "Data Source=spark.configcatalog.db";

builder.Services.AddDbContextFactory<SparkConfigDbContext>(opt =>
{
    if (conn.Contains("Data Source=", StringComparison.OrdinalIgnoreCase))
        opt.UseSqlite(conn);
    else
        opt.UseSqlServer(conn);
});

// --- Config catalog services ---
builder.Services.AddSingleton<IConfigProvider, DbConfigProvider>();

builder.Services.AddSingleton<RateLimitConfigAccessor>();
builder.Services.AddSingleton<IRateLimitConfigAccessor>(sp => sp.GetRequiredService<RateLimitConfigAccessor>());

builder.Services.AddHostedService<RateLimitConfigWarmupHostedService>();

// --- Rate limiting configured from DB-backed accessor ---
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    o.OnRejected = async (context, ct) =>
    {
        var http = context.HttpContext;
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        http.Response.ContentType = "application/problem+json";

        // Don't leak internal counters; just tell clients to retry later.
        await http.Response.WriteAsJsonAsync(new
        {
            type = "https://errors.spark.example.com/security/rate-limited",
            title = "Too many requests.",
            status = StatusCodes.Status429TooManyRequests,
            detail = "Slow down and retry later.",
            errorCode = "rate_limited",
            traceId = http.TraceIdentifier
        }, ct);
    };

    // Global limiter: prefer user -> client -> ip
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var key =
            RateLimitKeyFactory.GetUserKey(ctx) != "user:anonymous" ? RateLimitKeyFactory.GetUserKey(ctx)
            : RateLimitKeyFactory.GetClientKey(ctx) != "client:anonymous" ? RateLimitKeyFactory.GetClientKey(ctx)
            : RateLimitKeyFactory.GetIpFallback(ctx);

        var limits = ctx.RequestServices.GetRequiredService<IRateLimitConfigAccessor>().Global;

        return RateLimitPartition.GetTokenBucketLimiter(
            partitionKey: key,
            factory: _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = Math.Max(1, limits.BurstPer10Seconds),
                TokensPerPeriod = Math.Max(1, limits.PerIdentityPerMinute),
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            });
    });

    // Example policy: exports tenant fairness
    o.AddPolicy("exports-tenant", ctx =>
    {
        var tenantId = RateLimitKeyFactory.GetTenantKey(ctx);
        var enterprise = ctx.RequestServices.GetRequiredService<IRateLimitConfigAccessor>().GetEnterpriseForTenant(tenantId);

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: tenantId,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, enterprise.Exports.PerTenantPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

var app = builder.Build();

// Ensure DB exists + seed defaults (demo convenience).
// In SPARK you likely do migrations + admin seeding.
await using (var scope = app.Services.CreateAsyncScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SparkConfigDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await ConfigSeeder.EnsureSeededAsync(db, app.Lifetime.ApplicationStopping);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseRateLimiter();

// --- Demo endpoints ---
app.MapGet("/", () => Results.Ok(new { ok = true, message = "SPARK Config Catalog demo" }));

app.MapGet("/limits/current", (IRateLimitConfigAccessor a) => Results.Ok(new
{
    global = a.Global,
    enterprise = a.EnterpriseGlobal
}));

// A heavy endpoint to demonstrate policy usage
app.MapGet("/exports", () => Results.Ok(new { exported = true, atUtc = DateTime.UtcNow }))
   .RequireRateLimiting("exports-tenant");

// --- Admin: manage config entries (MVP). Secure this in real apps. ---
var admin = app.MapGroup("/admin/config");

admin.MapGet("/{conceptKey}", async (string conceptKey, IDbContextFactory<SparkConfigDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var concept = await db.ConfigConcepts
        .AsNoTracking()
        .Include(c => c.Entries)
        .SingleOrDefaultAsync(c => c.Key == conceptKey);

    return concept is null ? Results.NotFound() : Results.Ok(concept);
});

admin.MapPost("/{conceptKey}/{entryKey}", async (
    string conceptKey,
    string entryKey,
    AdminUpsertConfigEntryDto dto,
    IDbContextFactory<SparkConfigDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();

    var concept = await db.ConfigConcepts.SingleOrDefaultAsync(c => c.Key == conceptKey);
    if (concept is null)
    {
        concept = new Spark.ConfigCatalog.Infrastructure.Entities.ConfigConcept
        {
            Key = conceptKey,
            Name = conceptKey,
            IsEnabled = true,
            UpdatedAtUtc = DateTime.UtcNow
        };
        db.ConfigConcepts.Add(concept);
        await db.SaveChangesAsync();
    }

    var existing = await db.ConfigEntries.SingleOrDefaultAsync(e =>
        e.ConceptId == concept.Id && e.Key == entryKey && e.ScopeType == dto.ScopeType && e.ScopeKey == dto.ScopeKey);

    if (existing is null)
    {
        db.ConfigEntries.Add(new Spark.ConfigCatalog.Infrastructure.Entities.ConfigEntry
        {
            ConceptId = concept.Id,
            Key = entryKey,
            ValueType = dto.ValueType,
            Value = dto.Value,
            ScopeType = dto.ScopeType,
            ScopeKey = dto.ScopeKey,
            IsEnabled = dto.IsEnabled,
            ValidFromUtc = dto.ValidFromUtc,
            ValidToUtc = dto.ValidToUtc,
            UpdatedAtUtc = DateTime.UtcNow
        });
    }
    else
    {
        existing.ValueType = dto.ValueType;
        existing.Value = dto.Value;
        existing.IsEnabled = dto.IsEnabled;
        existing.ValidFromUtc = dto.ValidFromUtc;
        existing.ValidToUtc = dto.ValidToUtc;
        existing.UpdatedAtUtc = DateTime.UtcNow;
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

app.Run();

internal sealed record AdminUpsertConfigEntryDto(
    string ValueType,
    string Value,
    string ScopeType = "Global",
    string? ScopeKey = null,
    bool IsEnabled = true,
    DateTime? ValidFromUtc = null,
    DateTime? ValidToUtc = null);
