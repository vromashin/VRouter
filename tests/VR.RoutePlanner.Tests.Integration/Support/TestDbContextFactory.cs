using Microsoft.EntityFrameworkCore;
using VR.RoutePlanner.Infrastructure.Persistence;

namespace VR.RoutePlanner.Tests.Integration.Support;

/// <summary>
/// Фабрика контекстов для тестов, которым не нужен контейнер DI.
/// </summary>
internal sealed class TestDbContextFactory : IDbContextFactory<RoutePlannerDbContext>
{
    private readonly DbContextOptions<RoutePlannerDbContext> _options;

    public TestDbContextFactory(DbContextOptions<RoutePlannerDbContext> options)
    {
        _options = options;
    }

    public RoutePlannerDbContext CreateDbContext() => new(_options);
}
