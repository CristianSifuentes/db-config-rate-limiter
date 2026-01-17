namespace App.ConfigCatalog.Domain;

/// <summary>
/// Represents the scope for a config lookup. Scopes allow override precedence.
/// Typical precedence: User > Client > Tenant > Global.
/// </summary>
public readonly record struct ConfigScope(string ScopeType, string? ScopeKey)
{
    public static ConfigScope Global() => new("Global", null);
    public static ConfigScope Tenant(string tenantId) => new("Tenant", tenantId);
    public static ConfigScope Client(string clientId) => new("Client", clientId);
    public static ConfigScope User(string userId) => new("User", userId);

    public override string ToString() => $"{ScopeType}:{ScopeKey ?? "null"}";
}
