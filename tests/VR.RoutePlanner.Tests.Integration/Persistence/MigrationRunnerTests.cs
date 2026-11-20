using VR.RoutePlanner.Infrastructure.Persistence;

namespace VR.RoutePlanner.Tests.Integration.Persistence;

/// <summary>
/// Проверки <see cref="MigrationRunner"/> без подключения к базе.
/// </summary>
public sealed class MigrationRunnerTests
{
    [Fact]
    public void HistoryTable_IsServiceSpecific()
    {
        Assert.Equal("__EFMigrationsHistory_RoutePlanner", MigrationRunner.HistoryTable);
        Assert.NotEqual("__EFMigrationsHistory", MigrationRunner.HistoryTable);
    }

    [Fact]
    public void LockKey_MatchesExpectedName()
    {
        Assert.Equal("rms:migrations:routeplanner", MigrationRunner.LockKey);
    }

    [Fact]
    public void ComputeLockKey_IsStable()
    {
        var first = MigrationRunner.ComputeLockKey(MigrationRunner.LockKey);
        var second = MigrationRunner.ComputeLockKey(MigrationRunner.LockKey);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeLockKey_DifferentKeys_ProduceDifferentLocks()
    {
        var own = MigrationRunner.ComputeLockKey(MigrationRunner.LockKey);
        var foreign = MigrationRunner.ComputeLockKey("rms:migrations:wms");

        Assert.NotEqual(own, foreign);
    }

    [Fact]
    public void ComputeLockKey_EmptyKey_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => MigrationRunner.ComputeLockKey(" "));
    }

    [Fact]
    public void CreateHistoryTableSql_IsIdempotent()
    {
        var sql = MigrationRunner.CreateHistoryTableSql(MigrationRunner.HistoryTable);

        Assert.Contains("CREATE TABLE IF NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("__EFMigrationsHistory_RoutePlanner", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyHistoryTable_DiffersFromCurrent()
    {
        Assert.NotEqual(MigrationRunner.HistoryTable, MigrationRunner.LegacyHistoryTable);
    }
}
