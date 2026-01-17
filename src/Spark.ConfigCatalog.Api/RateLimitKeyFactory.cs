using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Spark.ConfigCatalog.Api
{
    public static class RateLimitKeyFactory
    {
        public static string GetTenantKey(HttpContext ctx)
            => ctx.User.FindFirstValue("tid")
               ?? "tenant:anonymous";

        public static string GetClientKey(HttpContext ctx)
            => ctx.User.FindFirstValue("azp")
               ?? ctx.User.FindFirstValue("appid")
               ?? "client:anonymous";

        public static string GetUserKey(HttpContext ctx)
            => ctx.User.FindFirstValue("oid")
               ?? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? "user:anonymous";

        public static string GetIpFallback(HttpContext ctx)
            => ctx.Connection.RemoteIpAddress?.ToString()
               ?? "ip:unknown";
    }

}
