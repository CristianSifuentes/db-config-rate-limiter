using App.ConfigCatalog.Domain;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;



namespace App.ConfigCatalog.Api
{
    public sealed class RateLimitAuditMiddleware : IMiddleware
    {
        private readonly ChannelWriter<RateLimitAuditEvent> _writer;

        public RateLimitAuditMiddleware(ChannelWriter<RateLimitAuditEvent> writer)
            => _writer = writer;

        public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
        {
            var started = DateTimeOffset.UtcNow;

            await next(ctx);

            // Policy name (best-effort). Si no hay metadata, usa "global".
            var policy = ResolvePolicyName(ctx) ?? "global";

            // Identity keys (elige UNA por policy: tenant/client/user/ip)
            // Para auditoría, normalmente registras 3 dimensiones (tenant+client+user/ip).
            var tenantKey = RateLimitKeyFactory.GetTenantKey(ctx);
            var clientKey = RateLimitKeyFactory.GetClientKey(ctx);
            var userKey = RateLimitKeyFactory.GetUserKey(ctx);
            var ipKey = RateLimitKeyFactory.GetIpFallback(ctx);

            var corr = ctx.Items.TryGetValue("X-Correlation-ID", out var c) ? c?.ToString() : null;

            var rejected = ctx.Response.StatusCode == StatusCodes.Status429TooManyRequests;

            // “Reason” puede venir del OnRejected (te explico abajo cómo).
            var reason = ctx.Items.TryGetValue("rate_limit_reason", out var r) ? r?.ToString() : null;

            // Publica 3 eventos (Tenant + Client + User/IP) o solo los que aplican.
            Publish(policy, "Tenant", tenantKey);
            Publish(policy, "Client", clientKey);
            Publish(policy, userKey != "user:anonymous" ? "User" : "Ip", userKey != "user:anonymous" ? userKey : ipKey);

            void Publish(string pol, string kind, string key)
            {
                // reduce cardinalidad: si es unknown/anonymous, igual registra
                _writer.TryWrite(new RateLimitAuditEvent(
                    AtUtc: started,
                    Policy: pol,
                    IdentityKind: kind,
                    IdentityKey: key,
                    Method: ctx.Request.Method,
                    Path: ctx.Request.Path.Value ?? "",
                    StatusCode: ctx.Response.StatusCode,
                    TraceId: ctx.TraceIdentifier,
                    CorrelationId: corr,
                    Rejected: rejected,
                    RetryAfterSeconds: TryGetRetryAfter(ctx),
                    Reason: reason));
            }

            static int? TryGetRetryAfter(HttpContext ctx)
            {
                if (!ctx.Response.Headers.TryGetValue("Retry-After", out var v)) return null;
                return int.TryParse(v.ToString(), out var s) ? s : null;
            }

            static string? ResolvePolicyName(HttpContext ctx)
            {
                // Best-effort: minimal APIs + rate limiting metadata.
                // Si estás en .NET 8 con Microsoft.AspNetCore.RateLimiting, usualmente hay metadata con PolicyName.
                var ep = ctx.GetEndpoint();
                if (ep is null) return null;

                // Evita acoplarte a tipos internos: busca una propiedad "PolicyName" por reflexión si hace falta.
                foreach (var m in ep.Metadata)
                {
                    var t = m.GetType();
                    var p = t.GetProperty("PolicyName");
                    if (p?.PropertyType == typeof(string))
                    {
                        var name = p.GetValue(m) as string;
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                }

                return null;
            }
        }
    }


}

