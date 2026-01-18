using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using App.ConfigCatalog.Api.Security;
using App.ConfigCatalog.Infrastructure;

namespace App.ConfigCatalog.Api.Middleware;

public sealed class RateLimitBlockMiddleware : IMiddleware
{
    private readonly IDbContextFactory<AppConfigDbContext> _dbFactory;

    public RateLimitBlockMiddleware(IDbContextFactory<AppConfigDbContext> dbFactory)
        => _dbFactory = dbFactory;

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        var now = DateTime.UtcNow;

        // Decide qué dimensiones vas a bloquear.
        // Recomendación:
        // - Siempre: Ip
        // - Si hay auth: Tenant / Client / User
        var candidates = BuildCandidates(ctx);

        await using var db = await _dbFactory.CreateDbContextAsync(ctx.RequestAborted);

        foreach (var c in candidates)
        {
            var hash = Sha256(c.Key);

            var blockedUntil = await db.RateLimitBlocks
                .AsNoTracking()
                .Where(b => b.IdentityKind == c.Kind
                         && b.IdentityHash == hash
                         && b.BlockedUntilUtc > now)
                .Select(b => (DateTime?)b.BlockedUntilUtc)
                .FirstOrDefaultAsync(ctx.RequestAborted);

            if (blockedUntil is null) continue;

            // --- Short-circuit ---
            var retryAfter = Math.Max(1, (int)Math.Ceiling((blockedUntil.Value - now).TotalSeconds));

            ctx.Items["rate_limit_reason"] = $"blocked_{c.Kind.ToLowerInvariant()}";
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden; // “blocked” suele ser 403 (policy/security)
            ctx.Response.Headers["Retry-After"] = retryAfter.ToString();

            await ctx.Response.WriteAsJsonAsync(new
            {
                type = "https://errors.app.example.com/security/blocked",
                title = "Request blocked.",
                status = 403,
                detail = "This identity is temporarily blocked.",
                reason = ctx.Items["rate_limit_reason"],
                retryAfterSeconds = retryAfter,
                traceId = ctx.TraceIdentifier
            }, ctx.RequestAborted);

            return;
        }

        await next(ctx);

        static byte[] Sha256(string s)
            => SHA256.HashData(Encoding.UTF8.GetBytes(s));

        static (string Kind, string Key)[] BuildCandidates(HttpContext ctx)
        {
            // Ip: siempre
            var ip = RateLimitKeyFactory.GetIpFallback(ctx);     // "ip:..."
            var list = new List<(string Kind, string Key)>
            {
                ("Ip", ip)
            };

            // Si hay identidad (token válido), agrega Tenant/Client/User
            // (Porque tu RateLimitKeyFactory ya contempla old/new claims)
            var tenant = RateLimitKeyFactory.GetTenantKey(ctx);  // "tenant:..." o "tenant:anonymous"
            if (!tenant.EndsWith("anonymous", StringComparison.OrdinalIgnoreCase))
                list.Add(("Tenant", tenant));

            var client = RateLimitKeyFactory.GetClientKey(ctx);  // "client:..." o "client:anonymous"
            if (!client.EndsWith("anonymous", StringComparison.OrdinalIgnoreCase))
                list.Add(("Client", client));

            var user = RateLimitKeyFactory.GetUserKey(ctx);      // "user:..." o "user:anonymous"
            if (!user.EndsWith("anonymous", StringComparison.OrdinalIgnoreCase))
                list.Add(("User", user));

            return list.ToArray();
        }
    }
}
