using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace App.ConfigCatalog.Infrastructure.Entities;

[Table("ConfigConcepts")]
public sealed class ConfigConcept
{
    public int Id { get; set; }

    [Column(TypeName = "varchar(100)")]
    public string Key { get; set; } = default!; // e.g. "rate_limits"

    [Column(TypeName = "varchar(100)")]
    public string Name { get; set; } = default!;

    [Column(TypeName = "varchar(1000)")]
    public string? Description { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [Timestamp]
    public byte[] RowVersion { get; set; } = default!;

    public List<ConfigEntry> Entries { get; set; } = new();
}
