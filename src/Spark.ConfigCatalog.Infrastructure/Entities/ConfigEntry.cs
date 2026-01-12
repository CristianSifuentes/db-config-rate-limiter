using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Spark.ConfigCatalog.Infrastructure.Entities;

[Table("ConfigEntries")]
public sealed class ConfigEntry
{
    public int Id { get; set; }

    public int ConceptId { get; set; }
    public ConfigConcept Concept { get; set; } = default!;

    [Column(TypeName = "varchar(200)")]
    public string Key { get; set; } = default!; // e.g. "global", "enterprise"

    [Column(TypeName = "varchar(32)")]
    public string ValueType { get; set; } = "json"; // int/string/bool/json

    [Column(TypeName = "varchar(max)")]
    public string Value { get; set; } = default!; // JSON or scalar

    [Column(TypeName = "varchar(20)")]
    public string ScopeType { get; set; } = "Global"; // Global/Tenant/Client/User

    [Column(TypeName = "varchar(100)")]
    public string? ScopeKey { get; set; } // null for Global

    public bool IsEnabled { get; set; } = true;

    public DateTime? ValidFromUtc { get; set; }
    public DateTime? ValidToUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [Timestamp]
    public byte[] RowVersion { get; set; } = default!;
}
