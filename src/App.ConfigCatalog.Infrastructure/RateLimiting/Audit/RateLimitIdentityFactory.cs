using App.ConfigCatalog.Domain;
using App.ConfigCatalog.Infrastructure.Entities;
using System.Security.Cryptography;
using System.Text;

namespace App.ConfigCatalog.Infrastructure.RateLimiting.Audit;

public static class RateLimitIdentityFactory
{
    public static RateLimitIdentity FromEvent(RateLimitAuditEvent e)
    {
        // IdentityKey expected: "tenant:xxx" | "client:yyy" | "user:zzz" | "ip:1.2.3.4"
        var (prefix, value) = SplitKey(e.IdentityKey);

        // Normaliza kind (usa IdentityKind si viene bien; si no, deriva del prefix)
        var kind = !string.IsNullOrWhiteSpace(e.IdentityKind)
            ? e.IdentityKind
            : PrefixToKind(prefix);

        // Hash seguro (indexable) — NUNCA indexes por plain
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(e.IdentityKey));

        // KeyPlain: opcional y recomendado enmascarar (para IP o user)
        var keyPlain = MaskKeyPlain(kind, value);

        var row = new RateLimitIdentity
        {
            Kind = kind,
            KeyHash = hash,
            KeyPlain = keyPlain,
            CreatedAtUtc = DateTime.UtcNow
        };

        // Columnas auxiliares (útiles para UI/joins). OJO con PII.
        switch (kind)
        {
            case "Tenant":
                row.TenantId = value;
                break;
            case "Client":
                row.ClientId = value;
                break;
            case "User":
                row.UserId = value;
                break;
            case "Ip":
                row.Ip = value;
                break;
        }

        return row;
    }

    private static (string prefix, string value) SplitKey(string identityKey)
    {
        var idx = identityKey.IndexOf(':');
        if (idx <= 0 || idx >= identityKey.Length - 1)
            return ("unknown", identityKey);

        return (identityKey[..idx], identityKey[(idx + 1)..]);
    }

    private static string PrefixToKind(string prefix) => prefix.ToLowerInvariant() switch
    {
        "tenant" => "Tenant",
        "client" => "Client",
        "user" => "User",
        "ip" => "Ip",
        _ => "Unknown"
    };

    // Enmascarado simple: evita PII directa en DB (ajústalo a tu compliance)
    private static string? MaskKeyPlain(string kind, string value) => kind switch
    {
        "Ip" => MaskIp(value),
        "User" => value.Length <= 8 ? "***" : $"{value[..4]}…{value[^4..]}",
        "Client" => value.Length <= 8 ? value : $"{value[..4]}…{value[^4..]}",
        "Tenant" => value, // normalmente no es PII
        _ => null
    };

    private static string MaskIp(string ip)
    {
        // IPv4: 10.20.30.40 -> 10.20.*.*
        var parts = ip.Split('.');
        return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.*.*" : "***";
    }
}
