using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VR.RoutePlanner.Infrastructure.Persistence;

namespace VR.RoutePlanner.Tests.Unit.Support;

/// <summary>
/// Фабрика контекстов на SQLite в памяти: схема создается из модели, соединение живет до освобождения фабрики.
/// </summary>
internal sealed class SqliteContextFactory : IDbContextFactory<RoutePlannerDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RoutePlannerDbContext> _options;

    private SqliteContextFactory(SqliteConnection connection, DbContextOptions<RoutePlannerDbContext> options)
    {
        _connection = connection;
        _options = options;
    }

    public static SqliteContextFactory Create()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<RoutePlannerDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var context = new RoutePlannerDbContext(options))
        {
            context.Database.EnsureCreated();
        }

        return new SqliteContextFactory(connection, options);
    }

    public RoutePlannerDbContext CreateDbContext() => new(_options);

    public ValueTask<RoutePlannerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CreateDbContext());

    public void Dispose() => _connection.Dispose();
}
