using System.ComponentModel.DataAnnotations.Schema;

namespace App.ConfigCatalog.Infrastructure.Entities;

[Table("RateLimitViolation")]
public sealed class RateLimitViolation
{
    public long Id { get; set; } // BIGINT IDENTITY

    [Column(TypeName = "datetime2")]
    public DateTime AtUtc { get; set; } // DEFAULT SYSUTCDATETIME()

    [Column(TypeName = "nvarchar(64)")]
    public string Policy { get; set; } = default!;

    [Column(TypeName = "nvarchar(20)")]
    public string IdentityKind { get; set; } = default!;

    [Column(TypeName = "varbinary(32)")]
    public byte[] IdentityHash { get; set; } = default!;

    [Column(TypeName = "nvarchar(64)")]
    public string? TraceId { get; set; }

    [Column(TypeName = "nvarchar(64)")]
    public string? CorrelationId { get; set; }

    [Column(TypeName = "nvarchar(512)")]
    public string? Path { get; set; }

    [Column(TypeName = "nvarchar(8)")]
    public string? Method { get; set; }

    public int StatusCode { get; set; } // 429
    public int? RetryAfterSeconds { get; set; }

    [Column(TypeName = "nvarchar(64)")]
    public string? Reason { get; set; } // e.g. global_tokenbucket_empty
}
