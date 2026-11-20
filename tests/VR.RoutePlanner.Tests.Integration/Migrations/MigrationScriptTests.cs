using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using VR.RoutePlanner.Infrastructure.Migrations;
using VR.RoutePlanner.Infrastructure.Persistence;
using VR.RoutePlanner.Tests.Integration.Support;

namespace VR.RoutePlanner.Tests.Integration.Migrations;

/// <summary>
/// Проверки SQL миграции этапа: идемпотентность и границы владения таблицами.
/// </summary>
public sealed class MigrationScriptTests
{
    private static readonly string[] OwnTables =
    {
        "vrt_MapVersion",
        "vrt_TrafficResource",
        "vrt_TrafficResourceEdge",
        "vrt_TrafficResourceNode",
        "vrt_ResourceConflict",
    };

    [Fact]
    public void GenerateScript_CreatesOwnTablesIdempotently()
    {
        using var context = CreateContext();

        var script = context.GetService<IMigrator>().GenerateScript();

        foreach (var table in OwnTables)
        {
            Assert.Contains(
                $"CREATE TABLE IF NOT EXISTS public.\"{table}\"",
                script,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GenerateScript_DoesNotTouchForeignTables()
    {
        using var context = CreateContext();

        var script = context.GetService<IMigrator>().GenerateScript();

        Assert.DoesNotContain("CREATE TABLE public.\"Maps\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE public.\"Nodes\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE public.\"Edges\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE public.\"Maps\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE public.\"Nodes\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE public.\"Edges\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE IF EXISTS public.\"Maps\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateScript_UsesServiceHistoryTable()
    {
        using var context = CreateContext();

        var script = context.GetService<IMigrator>().GenerateScript();

        Assert.Contains("__EFMigrationsHistory_RoutePlanner", script, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateScript_DropsOnlyOwnTables()
    {
        using var context = CreateContext();

        var downScript = context.GetService<IMigrator>().GenerateScript(
            fromMigration: Stage1Graph.MigrationId,
            toMigration: "0");

        foreach (var table in OwnTables)
        {
            Assert.Contains($"DROP TABLE IF EXISTS public.\"{table}\"", downScript, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("DROP TABLE IF EXISTS public.\"Maps\"", downScript, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE IF EXISTS public.\"Nodes\"", downScript, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE IF EXISTS public.\"Edges\"", downScript, StringComparison.Ordinal);
    }

    private static RoutePlannerDbContext CreateContext()
    {
        var options = TestDatabase.BuildOptions(
            "Host=127.0.0.1;Port=5432;Database=rms;Username=placeholder;Password=placeholder");

        return new RoutePlannerDbContext(options);
    }
}
