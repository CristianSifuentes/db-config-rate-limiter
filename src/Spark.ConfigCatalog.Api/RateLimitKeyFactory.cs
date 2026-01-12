using System.Security.Claims;

namespace Spark.ConfigCatalog.Api;

public static class RateLimitKeyFactory
{
    public static string GetUserKey(HttpContext ctx)
    {
        var oid = ctx.User?.FindFirstValue("oid")
            ?? ctx.User?.FindFirstValue(ClaimTypes.NameIdentifier);

        return !string.IsNullOrWhiteSpace(oid) ? $"user:{oid}" : "user:anonymous";
    }

    public static string GetClientKey(HttpContext ctx)
    {
        var client = ctx.User?.FindFirstValue("azp")
            ?? ctx.User?.FindFirstValue("appid");

        return !string.IsNullOrWhiteSpace(client) ? $"client:{client}" : "client:anonymous";
    }

    public static string GetTenantKey(HttpContext ctx)
    {
        var tid = ctx.User?.FindFirstValue("tid");
        return !string.IsNullOrWhiteSpace(tid) ? tid : "tenant:anonymous";
    }

    public static string GetIpFallback(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        return !string.IsNullOrWhiteSpace(ip) ? $"ip:{ip}" : "ip:unknown";
    }
}
