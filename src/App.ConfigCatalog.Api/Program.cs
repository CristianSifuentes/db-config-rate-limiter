using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using App.ConfigCatalog.Api;
using App.ConfigCatalog.Domain;
using App.ConfigCatalog.Infrastructure;
using App.ConfigCatalog.Infrastructure.RateLimiting;
using App.ConfigCatalog.Infrastructure.RateLimiting.Interfaces;
using App.ConfigCatalog.Infrastructure.RateLimiting.Store;
using App.ConfigCatalog.Infrastructure.Services;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using static Microsoft.ApplicationInsights.MetricDimensionNames.TelemetryContext;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Application Insights (optional): enabled when APPLICATIONINSIGHTS_CONNECTION_STRING is set.
builder.Services.AddApplicationInsightsTelemetry();

builder.Services.AddMemoryCache();

// --- DB ---
// For the demo we use SQLite by default (easy local run) with optional SQL Server.
// In App, point this to your existing AppDbContext and add ConfigConcepts/ConfigEntries DbSets.
var conn = builder.Configuration.GetConnectionString("ConfigCatalog")
          ?? builder.Configuration.GetConnectionString("Sql")
          ?? "Data Source=App.configcatalog.db";

builder.Services.AddDbContextFactory<AppConfigDbContext>(opt =>
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


builder.Services.AddSingleton(Channel.CreateBounded<RateLimitAuditEvent>(
    new BoundedChannelOptions(50_000) { SingleReader = true, SingleWriter = false }));

builder.Services.AddSingleton(sp => sp.GetRequiredService<Channel<RateLimitAuditEvent>>().Writer);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Channel<RateLimitAuditEvent>>().Reader);

builder.Services.AddSingleton<RateLimitAuditMiddleware>();

builder.Services.AddSingleton<IRateLimitAuditStore, SqlServerRateLimitAuditStore>();

builder.Services.AddHostedService<RateLimitAuditWriterHostedService>();

//4) Bonus: diseño maestro-detalle(auditoría avanzada)

//Tu modelo queda “enterprise-grade” así:

//RateLimitIdentity(Maestro)
//Identidad estable(Tenant/Client/User/Ip) con KeyHash seguro.

//RateLimitMinuteAgg (Hechos / métrica)
//Conteo por minuto por policy + identity (ideal para dashboards).

//RateLimitViolation (Eventos)
//Cada 429 (o bloqueo) con razón, traceId, correlationId.

//RateLimitBlock (Estado)
//“bloqueos activos” (y hasta cuándo), con razón.

//builder.Services.AddRateLimiter(o => { o.AddConcurrencyLimiter() });

// --- Rate limiting configured from DB-backed accessor ---
#region Rate Limiting Old Verison
//builder.Services.AddRateLimiter(o =>
//{
//    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

//    o.OnRejected = async (context, ct) =>
//    {
//        var http = context.HttpContext;
//        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
//        http.Response.ContentType = "application/problem+json";

//        // Don't leak internal counters; just tell clients to retry later.
//        await http.Response.WriteAsJsonAsync(new
//        {
//            type = "https://errors.App.example.com/security/rate-limited",
//            title = "Too many requests.",
//            status = StatusCodes.Status429TooManyRequests,
//            detail = "Slow down and retry later.",
//            errorCode = "rate_limited",
//            traceId = http.TraceIdentifier
//        }, ct);
//    };

//    // Global limiter: prefer user -> client -> ip
//    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
//    {
//        var key =
//            RateLimitKeyFactory.GetUserKey(ctx) != "user:anonymous" ? RateLimitKeyFactory.GetUserKey(ctx)
//            : RateLimitKeyFactory.GetClientKey(ctx) != "client:anonymous" ? RateLimitKeyFactory.GetClientKey(ctx)
//            : RateLimitKeyFactory.GetIpFallback(ctx);

//        var limits = ctx.RequestServices.GetRequiredService<IRateLimitConfigAccessor>().Global;

//        return RateLimitPartition.GetTokenBucketLimiter(
//            partitionKey: key,
//            factory: _ => new TokenBucketRateLimiterOptions
//            {
//                TokenLimit = Math.Max(1, limits.BurstPer10Seconds),
//                TokensPerPeriod = Math.Max(1, limits.PerIdentityPerMinute),
//                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
//                QueueLimit = 0,
//                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
//                AutoReplenishment = true
//            });
//    });

//    // Example policy: exports tenant fairness
//    o.AddPolicy("exports-tenant", ctx =>
//    {
//        var tenantId = RateLimitKeyFactory.GetTenantKey(ctx);
//        var enterprise = ctx.RequestServices.GetRequiredService<IRateLimitConfigAccessor>().GetEnterpriseForTenant(tenantId);

//        return RateLimitPartition.GetFixedWindowLimiter(
//            partitionKey: tenantId,
//            factory: _ => new FixedWindowRateLimiterOptions
//            {
//                PermitLimit = Math.Max(1, enterprise.Exports.PerTenantPerMinute),
//                Window = TimeSpan.FromMinutes(1),
//                QueueLimit = 0,
//                AutoReplenishment = true
//            });
//    });


//});
#endregion

#region Rate Limiting New Version
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    o.OnRejected = async (context, ct) =>
    {
        var http = context.HttpContext;

        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        http.Response.ContentType = "application/problem+json";

        // Opcional: Retry-After (segundos). Para FixedWindow queda perfecto.
        http.Response.Headers["Retry-After"] = "60";

        await http.Response.WriteAsJsonAsync(new
        {
            type = "https://errors.App.example.com/security/rate-limited",
            title = "Too many requests.",
            status = StatusCodes.Status429TooManyRequests,
            detail = "Slow down and retry later.",
            errorCode = "rate_limited",
            traceId = http.TraceIdentifier
        }, ct);
    };

    // ---------------------------------------------------------------------
    // GLOBAL limiter (defense-in-depth): user -> client -> ip
    // - This applies to EVERYTHING (unless you decide to exclude certain routes).
    // ---------------------------------------------------------------------
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

    // =====================================================================
    // EXPORTS (heavy endpoints)
    // JSON:
    // exports: perTenantPerMinute=600, perClientPerMinute=300, perUserPerMinute=120
    // =====================================================================

    o.AddPolicy("exports-tenant", ctx =>
    {
        var tenantId = RateLimitKeyFactory.GetTenantKey(ctx);
        var ent = ctx.RequestServices.GetRequiredService<IRateLimitConfigAccessor>()
            .GetEnterpriseForTenant(tenantId);

        // key estable
        var key = string.IsNullOrWhiteSpace(tenantId) ? "tenant:unknown" : tenantId;

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: key,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, ent.Exports.PerTenantPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    o.AddPolicy("exports-client", ctx =>
    {
        var clientKey = RateLimitKeyFactory.GetClientKey(ctx); // e.g. "client:{azp}"
        var key = clientKey != "client:anonymous"
            ? clientKey
            : RateLimitKeyFactory.GetIpFallback(ctx); // fallback anti-abuse

        var accessor = ctx.RequestServices.GetRequiredService<IRateLimitConfigAccessor>();
        var ent = key.StartsWith("client:", StringComparison.OrdinalIgnoreCase)
            ? accessor.GetEnterpriseForClient(key["client:".Length..])
            : accessor.EnterpriseGlobal;

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: key,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, ent.Exports.PerClientPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    o.AddPolicy("exports-user", ctx =>
    {
        var userKey = RateLimitKeyFactory.GetUserKey(ctx); // e.g. "user:{oid}"
        var key = userKey != "user:anonymous"
            ? userKey
            : RateLimitKeyFactory.GetIpFallback(ctx);

        // In App: If your rate tier per user depends on the tenant (common), use tenant:

        var tenantId = RateLimitKeyFactory.GetTenantKey(ctx);
        var ent = ctx.RequestServices.GetRequiredService<IRateLimitConfigAccessor>()
            .GetEnterpriseForTenant(tenantId);

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: key,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, ent.Exports.PerUserPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    // =====================================================================
    // SEARCH (query endpoints)
    // JSON:
    // search: perTenantPerMinute=900, perClientPerMinute=600, perUserPerMinute=240
    // =====================================================================

    o.AddPolicy("search-tenant", ctx =>
    {
        var tenantId = RateLimitKeyFactory.GetTenantKey(ctx);
        var ent = ctx.RequestServices.GetRequiredService<IRateLimitConfigAccessor>()
            .GetEnterpriseForTenant(tenantId);

        var key = string.IsNullOrWhiteSpace(tenantId) ? "tenant:unknown" : tenantId;

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: key,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, ent.Search.PerTenantPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    o.AddPolicy("search-client", ctx =>
    {
        var clientKey = RateLimitKeyFactory.GetClientKey(ctx);
        var key = clientKey != "client:anonymous"
            ? clientKey
            : RateLimitKeyFactory.GetIpFallback(ctx);

        var accessor = ctx.RequestServices.GetRequiredService<IRateLimitConfigAccessor>();
        var ent = key.StartsWith("client:", StringComparison.OrdinalIgnoreCase)
            ? accessor.GetEnterpriseForClient(key["client:".Length..])
            : accessor.EnterpriseGlobal;

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: key,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, ent.Search.PerClientPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    o.AddPolicy("search-user", ctx =>
    {
        var userKey = RateLimitKeyFactory.GetUserKey(ctx);
        var key = userKey != "user:anonymous"
            ? userKey
            : RateLimitKeyFactory.GetIpFallback(ctx);

        var tenantId = RateLimitKeyFactory.GetTenantKey(ctx);
        var ent = ctx.RequestServices.GetRequiredService<IRateLimitConfigAccessor>()
            .GetEnterpriseForTenant(tenantId);

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: key,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, ent.Search.PerUserPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    // =====================================================================
    // LOGIN (credential stuffing protection)
    // JSON:
    // login: perIpPerMinute=30, perClientPerMinute=60
    // =====================================================================

    o.AddPolicy("login-ip", ctx =>
    {
        var ip = RateLimitKeyFactory.GetIpFallback(ctx);
        var ent = ctx.RequestServices.GetRequiredService<IRateLimitConfigAccessor>().EnterpriseGlobal;

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ip,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, ent.Login.PerIpPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    o.AddPolicy("login-client", ctx =>
    {
        var clientKey = RateLimitKeyFactory.GetClientKey(ctx);
        var key = clientKey != "client:anonymous"
            ? clientKey
            : RateLimitKeyFactory.GetIpFallback(ctx);

        var accessor = ctx.RequestServices.GetRequiredService<IRateLimitConfigAccessor>();
        var ent = key.StartsWith("client:", StringComparison.OrdinalIgnoreCase)
            ? accessor.GetEnterpriseForClient(key["client:".Length..])
            : accessor.EnterpriseGlobal;

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: key,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, ent.Login.PerClientPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

#endregion


var app = builder.Build();

// Ensure DB exists + seed defaults (demo convenience).
// In App you likely do migrations + admin seeding.
await using (var scope = app.Services.CreateAsyncScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppConfigDbContext>>();
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
#region Security Middleware (Pre-Auth)
app.UseMiddleware<RateLimitAuditMiddleware>();

#endregion

#region End Points
// --- Demo endpoints ---
app.MapGet("/", () => Results.Ok(new { ok = true, message = "App Config Catalog demo" }));

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

admin.MapGet("/{conceptKey}", async (string conceptKey, IDbContextFactory<AppConfigDbContext> dbFactory) =>
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
    IDbContextFactory<AppConfigDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();

    var concept = await db.ConfigConcepts.SingleOrDefaultAsync(c => c.Key == conceptKey);
    if (concept is null)
    {
        concept = new App.ConfigCatalog.Infrastructure.Entities.ConfigConcept
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
        db.ConfigEntries.Add(new App.ConfigCatalog.Infrastructure.Entities.ConfigEntry
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

#endregion

internal sealed record AdminUpsertConfigEntryDto(
    string ValueType,
    string Value,
    string ScopeType = "Global",
    string? ScopeKey = null,
    bool IsEnabled = true,
    DateTime? ValidFromUtc = null,
    DateTime? ValidToUtc = null);
