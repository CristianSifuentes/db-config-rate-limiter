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

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen();

// Application Insights (optional): enabled when APPLICATIONINSIGHTS_CONNECTION_STRING is set.
builder.Services.AddApplicationInsightsTelemetry();

builder.Services.AddMemoryCache();

#region Configuration (Options & Entra)

var azureAd = builder.Configuration.GetSection("AzureAd");

// In Entra ID multi-tenant APIs, you typically use:
// - "common" (consumer + org accounts), or
// - "organizations", or
// - a specific tenantId.
// Use with caution based on your product’s tenancy model.
var instance = azureAd["Instance"] ?? "https://login.microsoftonline.com/";
var tenantId = azureAd["TenantId"] ?? "common";
var authority = $"{instance.TrimEnd('/')}/{tenantId}/v2.0";

// Audience is typically api://{API_CLIENT_ID} (recommended).
var audience =
    azureAd["Audience"] ??
    throw new InvalidOperationException("AzureAd:Audience is required (e.g., api://{API_CLIENT_ID}).");


#endregion


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
#region Authentication (JWT Bearer) — Hardened Validation

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.RequireHttpsMetadata = true;

        // Read hardening options once (fallback to defaults).
        var hardening =
            builder.Configuration.GetSection("TokenHardening").Get<TokenHardeningOptions>() ?? new();

        options.TokenValidationParameters = new TokenValidationParameters
        {
            // Require cryptographic integrity and expiration.
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            RequireExpirationTime = true,

            // Issuer must be from allow-list logic (multi-tenant safe).
            ValidateIssuer = true,
            IssuerValidator = TenantAllowListIssuerValidator.Build(builder.Configuration),

            // Strict audience validation to prevent token substitution across APIs.
            ValidateAudience = true,
            ValidAudiences = new[]
            {
                audience,
                azureAd["ClientId"] // optional fallback, if you also accept clientId as aud (be deliberate).
            }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray(),

            // Enforce exp/nbf with minimal skew to tolerate clock drift.
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(Math.Clamp(hardening.ClockSkewSeconds, 0, 120)),

            // Algorithm allow-list. For Entra access tokens RS256 is standard; ES256 can be enabled if applicable.
            ValidAlgorithms = new[]
            {
                SecurityAlgorithms.RsaSha256,   // RS256
                SecurityAlgorithms.EcdsaSha256  // ES256 (optional)
            },

            // Keep claims aligned with Entra.
            NameClaimType = "name",
            RoleClaimType = "roles",
        };

        // Key rollover safe (kid rotates).
        options.RefreshOnIssuerKeyNotFound = true;

        // Avoid persisting tokens server-side.
        options.SaveToken = false;

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                // Defense-in-depth: explicitly reject alg=none and unknown algorithms.
                if (ctx.SecurityToken is JwtSecurityToken jwt)
                {
                    var alg = jwt.Header.Alg;

                    if (string.Equals(alg, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Fail("Rejected unsigned JWT (alg=none).");
                        return;
                    }

                    if (alg is null ||
                        !(alg.Equals("RS256", StringComparison.OrdinalIgnoreCase) ||
                          alg.Equals("ES256", StringComparison.OrdinalIgnoreCase)))
                    {
                        ctx.Fail($"Rejected JWT with unsupported alg='{alg}'.");
                        return;
                    }
                }

                // Optional token replay / revocation checks using jti.
                // NOTE: Entra access tokens may not always carry "jti"; treat it as best-effort unless you enforce it.
                var revocation = ctx.HttpContext.RequestServices.GetRequiredService<ITokenRevocationStore>();
                var hard = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<TokenHardeningOptions>>().Value;

                if (hard.EnableJtiReplayProtection)
                {
                    var jti = ctx.Principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);

                    if (!string.IsNullOrWhiteSpace(jti))
                    {
                        // 1) If revoked -> block.
                        if (await revocation.IsRevokedAsync(jti, ctx.HttpContext.RequestAborted))
                        {
                            ctx.Fail("Token has been revoked.");
                            return;
                        }

                        // 2) Replay detection -> block if "jti" observed before.
                        // Disable this if your usage model expects reusing the same access token frequently.
                        var replayOk = await revocation.TryMarkSeenAsync(
                            jti,
                            TimeSpan.FromMinutes(hard.JtiCacheMinutes),
                            ctx.HttpContext.RequestAborted);

                        if (!replayOk)
                        {
                            ctx.Fail("Token replay detected (jti reused).");
                            return;
                        }
                    }
                }
            },

            OnAuthenticationFailed = ctx =>
            {
                // Do not leak details; store a safe reason for ProblemDetails middleware.
                var reason =
                    ctx.Exception is SecurityTokenExpiredException ? "token_expired" :
                    ctx.Exception is SecurityTokenInvalidSignatureException ? "invalid_signature" :
                    ctx.Exception is SecurityTokenInvalidAudienceException ? "invalid_audience" :
                    ctx.Exception is SecurityTokenInvalidIssuerException ? "invalid_issuer" :
                    ctx.Exception is SecurityTokenException ? "token_invalid" :
                    "auth_failed";

                ctx.HttpContext.Items["auth_fail_reason"] = reason;
                ctx.HttpContext.Items["auth_failed"] = true;

                return Task.CompletedTask;
            },

            OnChallenge = ctx =>
            {
                // Challenge occurs on 401, usually when no/invalid token.
                ctx.HttpContext.Items["auth_fail_reason"] ??= "challenge";
                return Task.CompletedTask;
            },

            OnForbidden = ctx =>
            {
                // Forbidden occurs on 403, usually when token valid but lacks required permission.
                ctx.HttpContext.Items["auth_fail_reason"] ??= "forbidden_policy";
                return Task.CompletedTask;
            }
        };
    });

#endregion

#region Authorization (Policies) — Scopes + App Roles

builder.Services.AddAuthorization(options =>
{
    // Centralized configuration: keep policy definitions in one place.
    //AuthzPolicies.Configure(options, builder.Configuration);

    #region Relevant1
    options.AddPolicy("Public", policy =>
    {
        policy.RequireAuthenticatedUser();

        #region  validate that it comes from specific clients (azp/appid) 
        // Optional: validate that it comes from specific clients (azp/appid)
        //policy.RequireAssertion(ctx =>
        //{
        //    var appId = ctx.User.FindFirst("azp")?.Value
        //               ?? ctx.User.FindFirst("appid")?.Value;

        //    var allowedClients = new[]
        //    {
        //          ApplicationContext.AppSettings.AzureAd.ClientId,
        //            ApplicationContext.AppSettings.AzureAd.Internal.ClientId
        //       };

        //    return !string.IsNullOrWhiteSpace(appId) && allowedClients.Contains(appId);
        //});


        #endregion

        policy.RequireAssertion(ctx =>
        {
            var scp = ctx.User.FindFirst("scp")?.Value ?? "";
            var scopes = scp.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // “old” claim en algunos stacks
            var scopesOld = ctx.User.Claims
                .Where(c => c.Type == "http://schemas.microsoft.com/identity/claims/scope")
                .SelectMany(c => (c.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToArray();

            var roles = ctx.User.FindAll("roles").Select(r => r.Value).ToArray();

            var allowedScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            "Documents.Read"
                        };

            var allowedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            "Reports.Read.All"
                        };

            return scopes.Any(allowedScopes.Contains)
                || scopesOld.Any(allowedScopes.Contains)
                || roles.Any(allowedRoles.Contains);
        });
    });
    //// Example "role-only" policy (use sparingly; scopes/app roles are usually better in multi-tenant APIs).
    //options.AddPolicy("AdminOnly", p =>
    //{
    //    p.RequireAuthenticatedUser();
    //    p.RequireRole("Admin");
    //});
    #endregion
});

#endregion


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
//   .RequireAuthorization(AuthzPolicies.PublicPolicyName);
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
.RequireAuthorization(AuthzPolicies.PublicPolicyName);



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
.RequireAuthorization(AuthzPolicies.PublicPolicyName);


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
