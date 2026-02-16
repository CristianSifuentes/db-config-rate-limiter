using App.ConfigCatalog.Domain.Extensions.Configurations;

namespace App.ConfigCatalog.Api;

public static class Startup
{
    public static void ConfigureSecurity(this WebApplicationBuilder builder)
    {
        Authentication.Configure(builder);
        Authorization.Configure(builder);
        RateLimitingNewVersion.Configure(builder);
    }
}
