using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using App.ConfigCatalog.Domain.RateLimit.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace App.ConfigCatalog.Domain.Extensions.Configurations;

public static class RateLimitingNewVersion
{
    public static void Configure(this WebApplicationBuilder builder)
    {
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                http.Response.ContentType = "application/problem+json";
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

            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                var key =
                    GetUserKey(ctx) != "user:anonymous" ? GetUserKey(ctx)
                    : GetClientKey(ctx) != "client:anonymous" ? GetClientKey(ctx)
                    : GetIpFallback(ctx);

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

            o.AddPolicy("exports-tenant", ctx =>
            {
                var tenantId = GetTenantKey(ctx);
                var ent = ctx.RequestServices.GetRequiredService<IRateLimitConfigAccessor>()
                    .GetEnterpriseForTenant(tenantId);

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
                var clientKey = GetClientKey(ctx);
                var key = clientKey != "client:anonymous"
                    ? clientKey
                    : GetIpFallback(ctx);

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
                var userKey = GetUserKey(ctx);
                var key = userKey != "user:anonymous"
                    ? userKey
                    : GetIpFallback(ctx);

                var tenantId = GetTenantKey(ctx);
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

            o.AddPolicy("search-tenant", ctx =>
            {
                var tenantId = GetTenantKey(ctx);
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
                var clientKey = GetClientKey(ctx);
                var key = clientKey != "client:anonymous"
                    ? clientKey
                    : GetIpFallback(ctx);

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
                var userKey = GetUserKey(ctx);
                var key = userKey != "user:anonymous"
                    ? userKey
                    : GetIpFallback(ctx);

                var tenantId = GetTenantKey(ctx);
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

            o.AddPolicy("login-ip", ctx =>
            {
                var ip = GetIpFallback(ctx);
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
                var clientKey = GetClientKey(ctx);
                var key = clientKey != "client:anonymous"
                    ? clientKey
                    : GetIpFallback(ctx);

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
    }

    private static string GetTenantKey(HttpContext ctx)
    {
        var tenantId = FirstValue(ctx.User, "tid", "http://schemas.microsoft.com/identity/claims/tenantid");
        return !string.IsNullOrWhiteSpace(tenantId)
            ? $"tenant:{tenantId}"
            : "tenant:anonymous";
    }

    private static string GetClientKey(HttpContext ctx)
    {
        var clientAppId = FirstValue(ctx.User, "azp", "appid");
        return !string.IsNullOrWhiteSpace(clientAppId)
            ? $"client:{clientAppId}"
            : "client:anonymous";
    }

    private static string GetUserKey(HttpContext ctx)
    {
        var userId = FirstValue(
            ctx.User,
            "oid",
            "http://schemas.microsoft.com/identity/claims/objectidentifier",
            "sub",
            ClaimTypes.NameIdentifier);

        return !string.IsNullOrWhiteSpace(userId)
            ? $"user:{userId}"
            : "user:anonymous";
    }

    private static string GetIpFallback(HttpContext ctx)
    {
        var ip = TryGetForwardedFor(ctx) ?? ctx.Connection.RemoteIpAddress?.ToString();
        return !string.IsNullOrWhiteSpace(ip)
            ? $"ip:{ip}"
            : "ip:unknown";
    }

    private static string? FirstValue(ClaimsPrincipal user, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var claim = user.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(claim))
            {
                return claim;
            }
        }

        return null;
    }

    private static string? TryGetForwardedFor(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var raw))
        {
            return null;
        }

        var first = raw.ToString().Split(',')[0].Trim();
        return IPAddress.TryParse(first, out _) ? first : null;
    }
}
