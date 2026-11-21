using Microsoft.EntityFrameworkCore;

namespace VR.RoutePlanner.Infrastructure.Legacy;

/// <summary>
/// Контекст только для чтения legacy-таблиц AGVControl. Миграции в нём не ведутся,
/// схему <c>public."Agvs"</c> создаёт и меняет AGVControl.
/// </summary>
public sealed class LegacyReadDbContext(DbContextOptions<LegacyReadDbContext> options) : DbContext(options)
{
    public DbSet<LegacyAgvRecord> Agvs => Set<LegacyAgvRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LegacyAgvRecord>(entity =>
        {
            entity.ToTable("Agvs", "public", table => table.ExcludeFromMigrations());
            entity.HasKey(record => record.Id);
        });
    }
}
