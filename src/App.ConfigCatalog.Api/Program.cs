using App.ConfigCatalog.Api;
using App.ConfigCatalog.Api.Middleware;
using App.ConfigCatalog.Api.Security;
using App.ConfigCatalog.Domain;
using App.ConfigCatalog.Infrastructure;
using App.ConfigCatalog.Infrastructure.RateLimiting;
using App.ConfigCatalog.Infrastructure.RateLimiting.Interfaces;
using App.ConfigCatalog.Infrastructure.RateLimiting.Store;
using App.ConfigCatalog.Infrastructure.Services;
using App.ConfigCatalog.Infrastructure.Token;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using static Microsoft.ApplicationInsights.MetricDimensionNames.TelemetryContext;
using Microsoft.AspNetCore.HttpOverrides;
using SPARK.Domain.Services;

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




#region Info
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
#endregion

//builder.Services.AddRateLimiter(o => { o.AddConcurrencyLimiter() });

// --- Rate limiting configured from DB-backed accessor ---

// Security configuration moved to modular startup composition.
builder.ConfigureSecurity();

#region Dependency Injection (Services + Middleware)
builder.Services.AddDistributedMemoryCache();

// --- Config catalog services ---
builder.Services.AddSingleton<IConfigProvider, DbConfigProvider>();

builder.Services.AddSingleton<RateLimitConfigAccessor>();
builder.Services.AddSingleton<IRateLimitConfigAccessor>(sp => sp.GetRequiredService<RateLimitConfigAccessor>());
builder.Services.AddSingleton<ITokenRevocationStore, DistributedTokenRevocationStore>();

builder.Services.AddHostedService<RateLimitConfigWarmupHostedService>();


builder.Services.AddSingleton(Channel.CreateBounded<RateLimitAuditEvent>(
    new BoundedChannelOptions(50_000) { SingleReader = true, SingleWriter = false }));

builder.Services.AddSingleton(sp => sp.GetRequiredService<Channel<RateLimitAuditEvent>>().Writer);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Channel<RateLimitAuditEvent>>().Reader);

builder.Services.AddSingleton<RateLimitAuditMiddleware>();
builder.Services.AddScoped<RateLimitBlockMiddleware>();

builder.Services.AddSingleton<IRateLimitAuditStore, SqlServerRateLimitAuditStore>();

builder.Services.AddHostedService<RateLimitAuditWriterHostedService>();
#endregion

#region Build App

var app = builder.Build();

#endregion

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

#region Security Middleware (Pre-Auth)

// 1) Auditar TODO (outer wrapper)
app.UseMiddleware<RateLimitAuditMiddleware>();    // wrapper para auditar TODO (incluye blocked)

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
#endregion


#region AuthN / AuthZ


// Authentication populates HttpContext.User.

// 2) AuthN primero (para poder bloquear por tenant/user/client con claims)
app.UseAuthentication();

// 3) Cortar temprano si está bloqueado (antes de RateLimiter)
app.UseMiddleware<RateLimitBlockMiddleware>();

// 4) Luego rate limiting
app.UseRateLimiter();


// Authorization enforces policies/scopes/roles.
// 5) AuthZ

app.UseAuthorization();
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
//app.MapGet("/exports", () => Results.Ok(new { exported = true, atUtc = DateTime.UtcNow }))
//   .RequireRateLimiting("exports-tenant")
//   .RequireAuthorization(App.ConfigCatalog.Domain.Extensions.Configurations.Authorization.PublicPolicyName);
app.MapGet("/exports", (
    HttpContext http,
    ClaimsPrincipal user) =>
{
    var tenant = TenantContextFactory.From(user);
    if (string.IsNullOrWhiteSpace(tenant.TenantId))
        return Results.Forbid();

    //// Validate BEFORE touching the data layer (cheap rejection).
    //var validation = SearchQueryValidator.Validate(q);
    //if (!validation.ok)
    //{
    //    return Results.BadRequest(new
    //    {
    //        error = "invalid_query",
    //        message = validation.error,
    //        traceId = http.TraceIdentifier
    //    });
    //}
    return Results.Ok();


})
.RequireRateLimiting("search-tenant")
.RequireAuthorization(App.ConfigCatalog.Domain.Extensions.Configurations.Authorization.PublicPolicyName);



app.MapGet("/search", (
    HttpContext http,
    ClaimsPrincipal user) =>
{
    var tenant = TenantContextFactory.From(user);
    if (string.IsNullOrWhiteSpace(tenant.TenantId))
        return Results.Forbid();

    //// Validate BEFORE touching the data layer (cheap rejection).
    //var validation = SearchQueryValidator.Validate(q);
    //if (!validation.ok)
    //{
    //    return Results.BadRequest(new
    //    {
    //        error = "invalid_query",
    //        message = validation.error,
    //        traceId = http.TraceIdentifier
    //    });
    //}
    return Results.Ok();


})
.RequireRateLimiting("search-tenant")
.RequireAuthorization(App.ConfigCatalog.Domain.Extensions.Configurations.Authorization.PublicPolicyName);


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
