namespace App.ConfigCatalog.Domain;

public sealed class RateLimitingEnterpriseOptions
{
    public EndpointLimits Exports { get; set; } = new();
    public EndpointLimits Search { get; set; } = new();
    public LoginLimits Login { get; set; } = new();

    public sealed class EndpointLimits
    {
        public int PerTenantPerMinute { get; set; } = 600;
        public int PerClientPerMinute { get; set; } = 300;
        public int PerUserPerMinute { get; set; } = 120;
    }

    public sealed class LoginLimits
    {
        public int PerIpPerMinute { get; set; } = 30;
        public int PerClientPerMinute { get; set; } = 60;
    }
}
