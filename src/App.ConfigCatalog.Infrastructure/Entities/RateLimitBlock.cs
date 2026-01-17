using System.ComponentModel.DataAnnotations.Schema;

namespace App.ConfigCatalog.Infrastructure.Entities;

[Table("RateLimitBlock")]
public sealed class RateLimitBlock
{
    public long Id { get; set; } // BIGINT IDENTITY

    [Column(TypeName = "nvarchar(20)")]
    public string IdentityKind { get; set; } = default!;

    [Column(TypeName = "varbinary(32)")]
    public byte[] IdentityHash { get; set; } = default!;

    [Column(TypeName = "nvarchar(128)")]
    public string? Reason { get; set; }

    [Column(TypeName = "datetime2")]
    public DateTime BlockedUntilUtc { get; set; }

    [Column(TypeName = "datetime2")]
    public DateTime CreatedAtUtc { get; set; } // DEFAULT SYSUTCDATETIME()
}
