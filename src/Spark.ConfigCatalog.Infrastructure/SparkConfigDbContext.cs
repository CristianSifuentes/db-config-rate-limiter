using Microsoft.EntityFrameworkCore;
using Spark.ConfigCatalog.Infrastructure.Entities;

namespace Spark.ConfigCatalog.Infrastructure;

/// <summary>
/// Minimal DbContext dedicated to the configuration catalog.
/// In SPARK, you can merge these DbSets into your existing SPARKDbContext instead.
/// </summary>
public sealed class SparkConfigDbContext : DbContext
{
    public SparkConfigDbContext(DbContextOptions<SparkConfigDbContext> options) : base(options) { }

    public DbSet<ConfigConcept> ConfigConcepts => Set<ConfigConcept>();
    public DbSet<ConfigEntry> ConfigEntries => Set<ConfigEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConfigConcept>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Key).IsUnique();
            b.Property(x => x.Key).IsRequired();
            b.Property(x => x.Name).IsRequired();
        });

        modelBuilder.Entity<ConfigEntry>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.ConceptId, x.Key, x.ScopeType, x.ScopeKey });
            b.Property(x => x.Key).IsRequired();
            b.Property(x => x.ValueType).IsRequired();
            b.Property(x => x.Value).IsRequired();
            b.HasOne(x => x.Concept)
             .WithMany(c => c.Entries)
             .HasForeignKey(x => x.ConceptId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
