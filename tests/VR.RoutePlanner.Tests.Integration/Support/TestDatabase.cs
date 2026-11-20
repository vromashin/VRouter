using Microsoft.EntityFrameworkCore;
using VR.RoutePlanner.Infrastructure.Persistence;

namespace VR.RoutePlanner.Tests.Integration.Support;

/// <summary>
/// Доступ к тестовой базе <c>rms</c> через строку подключения из окружения.
/// </summary>
internal static class TestDatabase
{
    /// <summary>Имя переменной окружения со строкой подключения.</summary>
    public const string ConnectionVariable = "VRP_RMS_CONNECTION";

    /// <summary>Внешний идентификатор карты, на которой идут проверки.</summary>
    public const string MapId = "801";

    /// <summary>Строка подключения либо <c>null</c>.</summary>
    public static string? ConnectionString => Environment.GetEnvironmentVariable(ConnectionVariable);

    /// <summary>Строка подключения для теста с пропуском, если переменной нет.</summary>
    public static string RequireConnection()
    {
        var value = ConnectionString;
        if (string.IsNullOrWhiteSpace(value))
        {
            Skip.If(true, $"Переменная окружения {ConnectionVariable} не задана, тест требует доступ к базе rms.");
        }

        return value!;
    }

    /// <summary>Создает фабрику контекстов с историей миграций сервиса.</summary>
    public static DbContextOptions<RoutePlannerDbContext> BuildOptions(string connectionString)
    {
        return new DbContextOptionsBuilder<RoutePlannerDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(MigrationRunner.HistoryTable))
            .Options;
    }
}
