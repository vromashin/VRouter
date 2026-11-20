using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VR.RoutePlanner.Infrastructure.Persistence;
using VR.RoutePlanner.Tests.Integration.Support;
using Xunit;

namespace VR.RoutePlanner.Tests.Integration.Persistence;

/// <summary>
/// Применение миграции этапа 1 на реальной базе <c>rms</c>: DDL выполняется по-настоящему,
/// поэтому дубль колонки или несовместимый тип валят тест.
/// Без переменной окружения <c>VRP_RMS_CONNECTION</c> тест пропускается.
/// </summary>
public sealed class MigrationApplyTests
{
    /// <summary>Таблицы, которые обязана создать миграция этапа 1.</summary>
    private static readonly string[] ExpectedTables =
    {
        "vrt_MapVersion",
        "vrt_EdgeWidth",
        "vrt_TrafficResource",
        "vrt_TrafficResourceEdge",
        "vrt_TrafficResourceNode",
        "vrt_ResourceConflict",
    };

    [SkippableFact]
    public async Task MigrateAsync_AppliesDdl_Idempotently()
    {
        var connectionString = TestDatabase.RequireConnection();
        await using var context = new RoutePlannerDbContext(TestDatabase.BuildOptions(connectionString));

        var applied = await MigrationRunner.MigrateAsync(
            context,
            MigrationRunner.HistoryTable,
            MigrationRunner.LockKey,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Empty(context.Database.GetPendingMigrations());
        Assert.True(applied >= 0);

        var tables = await context.Database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync();

        foreach (var expected in ExpectedTables)
        {
            Assert.True(tables.Contains(expected), $"В базе нет таблицы public.\"{expected}\".");
        }

        var historyRows = await CountHistoryRowsAsync(context);

        var appliedAgain = await MigrationRunner.MigrateAsync(
            context,
            MigrationRunner.HistoryTable,
            MigrationRunner.LockKey,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(0, appliedAgain);
        Assert.Equal(historyRows, await CountHistoryRowsAsync(context));
        Assert.Equal(1, historyRows);
    }

    private static async Task<int> CountHistoryRowsAsync(RoutePlannerDbContext context)
    {
        var sql = $"SELECT count(*)::int AS \"Value\" FROM public.\"{MigrationRunner.HistoryTable}\"";
        return await context.Database.SqlQueryRaw<int>(sql).SingleAsync();
    }
}
