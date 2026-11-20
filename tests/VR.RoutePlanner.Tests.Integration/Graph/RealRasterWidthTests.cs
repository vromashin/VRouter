using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Traffic;
using VR.RoutePlanner.Infrastructure.Graph;
using VR.RoutePlanner.Infrastructure.Persistence;
using VR.RoutePlanner.Tests.Integration.Support;
using Xunit;

namespace VR.RoutePlanner.Tests.Integration.Graph;

/// <summary>
/// Ширины ребер карты 801 по настоящему растру инфлированной карты препятствий.
/// Нужны <c>VRP_RMS_CONNECTION</c> и <c>VRP_MAP_RASTER</c> (полный путь к PNG), иначе тест пропускается.
/// </summary>
public sealed class RealRasterWidthTests
{
    /// <summary>Переменная окружения с полным путем к растру.</summary>
    public const string RasterVariable = "VRP_MAP_RASTER";

    /// <summary>Ожидаемое число ребер карты 801.</summary>
    private const int ExpectedEdgeCount = 365;

    [SkippableFact]
    public async Task ComputeWidthsAsync_RealRaster_ProducesNarrowAndWideCorridors()
    {
        var rasterPath = Environment.GetEnvironmentVariable(RasterVariable);
        var connectionString = TestDatabase.RequireConnection();

        if (string.IsNullOrWhiteSpace(rasterPath) || !File.Exists(rasterPath))
        {
            Skip.If(true, $"Переменная {RasterVariable} не задана или файл растра не найден.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(rasterPath!));
        var graphRepository = CreateGraphRepository(connectionString);
        var graph = await graphRepository.LoadAsync(TestDatabase.MapId, CancellationToken.None);

        var options = new RoutePlannerOptions
        {
            WidthSource = WidthSource.Raster,
            MapRasterPath = directory,
            InflationRadius = 0.4,
            ObstacleThreshold = 128,
            AgvProfiles = new Dictionary<string, AgvProfileOptions>(StringComparer.Ordinal)
            {
                ["tusk-forklift-1102"] = new()
                {
                    Name = "tusk-forklift-1102",
                    WidthMeters = 1.102,
                    LengthMeters = 1.455,
                    HeightMeters = 0.17,
                },
            },
            DefaultAgvProfile = "tusk-forklift-1102",
        };

        var builder = new TrafficResourceBuilder(
            new MapRasterProvider(
                new TestDbContextFactory(TestDatabase.BuildOptions(connectionString)),
                graphRepository,
                new MemoryCache(new MemoryCacheOptions()),
                Options.Create(options),
                NullLogger<MapRasterProvider>.Instance),
            Options.Create(options),
            NullLogger<TrafficResourceBuilder>.Instance);

        var widths = await builder.ComputeWidthsAsync(graph, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(ExpectedEdgeCount, widths.Count);
        Assert.All(widths, width => Assert.Equal(WidthSource.Raster, width.WidthSource));
        Assert.DoesNotContain(widths, width => Math.Abs(width.WidthMeters - options.DefaultCorridorWidth) < 1e-9);

        var min = widths.Min(width => width.WidthMeters);
        var max = widths.Max(width => width.WidthMeters);
        var average = widths.Average(width => width.WidthMeters);
        var summary = $"ширины растра: min = {min:F3}, avg = {average:F3}, max = {max:F3}";

        Assert.True(min > 0, $"Минимальная ширина должна быть больше нуля. {summary}");
        Assert.True(min < 2.0, $"В узком месте склада ширина должна быть меньше 2.0 м. {summary}");
        Assert.True(max > 2.0, $"В широком месте склада ширина должна быть больше 2.0 м. {summary}");

        var edges = graph.Edges.Values
            .SelectMany(list => list)
            .DistinctBy(edge => edge.Id)
            .ToDictionary(edge => edge.Id, edge => edge.EdgeId);

        var widthsByBusinessId = widths
            .Where(width => edges.ContainsKey(width.EdgeId))
            .ToDictionary(width => edges[width.EdgeId], width => width.WidthMeters, StringComparer.Ordinal);

        const string narrowEdgeId = "801-wa145-wa146";
        const string wideEdgeId = "801-wa145-wo-0-4";

        Assert.True(widthsByBusinessId.ContainsKey(narrowEdgeId), $"В наборе ширин нет ребра {narrowEdgeId}.");
        Assert.True(widthsByBusinessId.ContainsKey(wideEdgeId), $"В наборе ширин нет ребра {wideEdgeId}.");

        var narrow = widthsByBusinessId[narrowEdgeId];
        var wide = widthsByBusinessId[wideEdgeId];

        Assert.True(
            narrow < wide,
            $"Ребро {narrowEdgeId} должно быть уже ребра {wideEdgeId}: узкое = {narrow:F3}, широкое = {wide:F3}.");
    }

    private static WarehouseGraphRepository CreateGraphRepository(string connectionString)
    {
        var options = Options.Create(new RoutePlannerOptions { WidthSource = WidthSource.Default });

        return new WarehouseGraphRepository(
            new TestDbContextFactory(TestDatabase.BuildOptions(connectionString)),
            new MemoryCache(new MemoryCacheOptions()),
            options,
            NullLogger<WarehouseGraphRepository>.Instance);
    }
}
