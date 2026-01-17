using Microsoft.EntityFrameworkCore;
using Spark.ConfigCatalog.Infrastructure.Entities;
using Spark.ConfigCatalog.Infrastructure.RateLimiting.ReadModels;

namespace Spark.ConfigCatalog.Infrastructure;

/// <summary>
/// Minimal DbContext dedicated to the configuration catalog.
/// In SPARK, you can merge these DbSets into your existing SPARKDbContext instead.
/// </summary>
public sealed class SparkConfigDbContext : DbContext
{
    public SparkConfigDbContext(DbContextOptions<SparkConfigDbContext> options) : base(options) { }

    #region Entities (write-model)
    public DbSet<ConfigConcept> ConfigConcepts => Set<ConfigConcept>();
    public DbSet<ConfigEntry> ConfigEntries => Set<ConfigEntry>();

    // ✅ NEW: Rate limiting audit
    public DbSet<RateLimitIdentity> RateLimitIdentities => Set<RateLimitIdentity>();
    public DbSet<RateLimitMinuteAgg> RateLimitMinuteAggs => Set<RateLimitMinuteAgg>();
    public DbSet<RateLimitViolation> RateLimitViolations => Set<RateLimitViolation>();
    public DbSet<RateLimitBlock> RateLimitBlocks => Set<RateLimitBlock>();
    #endregion

    #region ReadModels (keyless / views)
    public DbSet<RateLimitViolationRow> RateLimitViolationRows => Set<RateLimitViolationRow>();

    public DbSet<RateLimitMinuteAggRow> RateLimitMinuteAggRows => Set<RateLimitMinuteAggRow>();
    #endregion
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

        // ============================================================
        // ✅ RateLimitIdentity
        // CREATE UNIQUE INDEX UX_RateLimitIdentity_KindHash ON RateLimitIdentity(Kind, KeyHash)
        // ============================================================
        modelBuilder.Entity<RateLimitIdentity>(b =>
        {
            b.HasKey(x => x.Id);

            b.Property(x => x.Kind).IsRequired();
            b.Property(x => x.KeyHash).IsRequired();

            b.HasIndex(x => new { x.Kind, x.KeyHash })
             .IsUnique()
             .HasDatabaseName("UX_RateLimitIdentity_KindHash");

            b.Property(x => x.CreatedAtUtc)
             .HasDefaultValueSql("SYSUTCDATETIME()");
        });

        // ============================================================
        // ✅ RateLimitMinuteAgg
        // PRIMARY KEY (WindowStartUtc, Policy, IdentityKind, IdentityHash)
        // ============================================================
        modelBuilder.Entity<RateLimitMinuteAgg>(b =>
        {
            b.HasKey(x => new { x.WindowStartUtc, x.Policy, x.IdentityKind, x.IdentityHash });

            b.Property(x => x.WindowStartUtc).IsRequired();
            b.Property(x => x.Policy).IsRequired();
            b.Property(x => x.IdentityKind).IsRequired();
            b.Property(x => x.IdentityHash).IsRequired();

            // Recomendación: índices opcionales para queries frecuentes
            b.HasIndex(x => new { x.Policy, x.WindowStartUtc })
             .HasDatabaseName("IX_RateLimitMinuteAgg_Policy_Window");

            b.HasIndex(x => new { x.IdentityKind, x.WindowStartUtc })
             .HasDatabaseName("IX_RateLimitMinuteAgg_Kind_Window");
        });

        // ============================================================
        // ✅ RateLimitViolation
        // CREATE INDEX IX_RateLimitViolation_At ON RateLimitViolation(AtUtc DESC)
        // ============================================================
        modelBuilder.Entity<RateLimitViolation>(b =>
        {
            b.HasKey(x => x.Id);

            b.Property(x => x.Policy).IsRequired();
            b.Property(x => x.IdentityKind).IsRequired();
            b.Property(x => x.IdentityHash).IsRequired();
            b.Property(x => x.StatusCode).IsRequired();

            b.Property(x => x.AtUtc)
             .HasDefaultValueSql("SYSUTCDATETIME()");

            b.HasIndex(x => x.AtUtc)
             .HasDatabaseName("IX_RateLimitViolation_At");
        });

        // ============================================================
        // ✅ RateLimitBlock
        // CREATE INDEX IX_RateLimitBlock_Until ON RateLimitBlock(BlockedUntilUtc)
        // ============================================================
        modelBuilder.Entity<RateLimitBlock>(b =>
        {
            b.HasKey(x => x.Id);

            b.Property(x => x.IdentityKind).IsRequired();
            b.Property(x => x.IdentityHash).IsRequired();
            b.Property(x => x.BlockedUntilUtc).IsRequired();

            b.Property(x => x.CreatedAtUtc)
             .HasDefaultValueSql("SYSUTCDATETIME()");

            b.HasIndex(x => x.BlockedUntilUtc)
             .HasDatabaseName("IX_RateLimitBlock_Until");

            // Opcional: evita duplicados "activos" por identidad (si quieres)
            b.HasIndex(x => new { x.IdentityKind, x.IdentityHash, x.BlockedUntilUtc })
             .HasDatabaseName("IX_RateLimitBlock_Identity_Until");
        });

        modelBuilder.Entity<RateLimitViolationRow>(b =>
        {
            b.HasNoKey();

            // Opción 1: map a VIEW (recomendado)
            // b.ToView("vw_RateLimitViolation");

            // Opción 2: si NO usas view, no lo mapees a nada
            // y úsalo solo con FromSql en un repositorio.

            b.Property(x => x.Policy).HasMaxLength(64);
            b.Property(x => x.IdentityKind).HasMaxLength(20);
            b.Property(x => x.Method).HasMaxLength(8);
            b.Property(x => x.Path).HasMaxLength(512);
        });

        // Read-model mapping (VIEW)
        modelBuilder.Entity<RateLimitMinuteAggRow>(b =>
        {
            b.HasNoKey();
            b.ToView("vw_RateLimitMinuteAgg"); // crea este VIEW en SQL (ideal)
        });

    }
}
