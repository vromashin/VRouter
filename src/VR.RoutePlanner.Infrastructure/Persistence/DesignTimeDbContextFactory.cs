using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VR.RoutePlanner.Infrastructure.Persistence;

/// <summary>
/// Фабрика контекста для <c>dotnet ef</c>. Строка подключения берется только из окружения,
/// в репозитории паролей нет.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RoutePlannerDbContext>
{
    /// <summary>Имя переменной окружения со строкой подключения к базе <c>rms</c>.</summary>
    public const string ConnectionVariable = "VRP_RMS_CONNECTION";

    /// <inheritdoc />
    public RoutePlannerDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Для design-time операций задайте строку подключения к базе rms в переменной окружения {ConnectionVariable}.");
        }

        var options = new DbContextOptionsBuilder<RoutePlannerDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(MigrationRunner.HistoryTable))
            .Options;

        return new RoutePlannerDbContext(options);
    }
}
