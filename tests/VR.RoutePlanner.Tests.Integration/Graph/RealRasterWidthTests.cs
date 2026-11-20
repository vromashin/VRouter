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
[Collection(VrtWriteCollection.Name)]
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

        // Ширина 2.000 м встречается на карте законно, поэтому проверяется происхождение
        // значения (растр и имя файла), а не совпадение с DefaultCorridorWidth.
        Assert.All(
            widths,
            width => Assert.False(
                string.IsNullOrWhiteSpace(width.WidthSourceDetail),
                "У ширины пустой WidthSourceDetail: значение взято не из растра."));
        Assert.All(
            widths,
            width => Assert.DoesNotContain(
                "Default",
                width.WidthSourceDetail!,
                StringComparison.OrdinalIgnoreCase));

        var min = widths.Min(width => width.WidthMeters);
        var max = widths.Max(width => width.WidthMeters);
        var average = widths.Average(width => width.WidthMeters);
        var summary = $"ширины растра: min = {min:F3}, avg = {average:F3}, max = {max:F3}";

        Assert.True(min > 0, $"Минимальная ширина должна быть больше нуля. {summary}");

        // На карте есть ребра уже габарита робота с боковыми зазорами: они непроходимы для профиля 1.102.
        var passableWidth = options.AgvProfiles[options.DefaultAgvProfile].WidthMeters
            + (2 * options.SideMargin);
        Assert.True(
            min < passableWidth,
            $"Минимальная ширина {min:F3} м должна быть меньше B + 2*S = {passableWidth:F3} м: "
            + "иначе на карте нет непроходимых для робота ребер. " + summary);

        // Те же ребра дают строки IsPassable = false в vrt_EdgePassability.
        Assert.Contains(widths, width => width.WidthMeters < passableWidth);

        // Открытые зоны склада дают ширины в десятки метров.
        Assert.True(
            max > 10.0,
            $"Максимальная ширина {max:F3} м должна быть больше 10.0 м: иначе открытых зон не найдено. {summary}");

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

        Assert.True(narrow > 0, $"Ширина ребра {narrowEdgeId} должна быть больше нуля: {narrow:F3}. {summary}");
        Assert.True(wide > 0, $"Ширина ребра {wideEdgeId} должна быть больше нуля: {wide:F3}. {summary}");
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
