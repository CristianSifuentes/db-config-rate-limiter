using System.ComponentModel.DataAnnotations.Schema;

namespace Spark.ConfigCatalog.Infrastructure.Entities;

[Table("RateLimitIdentity")]
public sealed class RateLimitIdentity
{
    public long Id { get; set; } // BIGINT IDENTITY

    [Column(TypeName = "nvarchar(20)")]
    public string Kind { get; set; } = default!; // Tenant | Client | User | Ip

    [Column(TypeName = "varbinary(32)")]
    public byte[] KeyHash { get; set; } = default!; // SHA-256

    [Column(TypeName = "nvarchar(256)")]
    public string? KeyPlain { get; set; } // opcional (solo si no es PII / enmascarado)

    [Column(TypeName = "nvarchar(64)")]
    public string? TenantId { get; set; }

    [Column(TypeName = "nvarchar(64)")]
    public string? ClientId { get; set; }

    [Column(TypeName = "nvarchar(64)")]
    public string? UserId { get; set; }

    [Column(TypeName = "nvarchar(64)")]
    public string? Ip { get; set; }

    [Column(TypeName = "datetime2")]
    public DateTime CreatedAtUtc { get; set; }
}
