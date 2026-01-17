using System.ComponentModel.DataAnnotations.Schema;

namespace App.ConfigCatalog.Infrastructure.Entities;

[Table("RateLimitMinuteAgg")]
public sealed class RateLimitMinuteAgg
{
    // PRIMARY KEY (WindowStartUtc, Policy, IdentityKind, IdentityHash)
    [Column(TypeName = "datetime2")]
    public DateTime WindowStartUtc { get; set; } // minuto truncado

    [Column(TypeName = "nvarchar(64)")]
    public string Policy { get; set; } = default!;

    [Column(TypeName = "nvarchar(20)")]
    public string IdentityKind { get; set; } = default!; // Tenant|Client|User|Ip

    [Column(TypeName = "varbinary(32)")]
    public byte[] IdentityHash { get; set; } = default!; // SHA-256

    [Column(TypeName = "nvarchar(256)")]
    public string? RouteTemplate { get; set; } // cuidado cardinalidad

    [Column(TypeName = "nvarchar(8)")]
    public string? Method { get; set; }

    public long Requests { get; set; }
    public long Rejected { get; set; } // 429

    public int? MaxObservedConcurrency { get; set; }
    public int? LastStatusCode { get; set; }
}
