using VR.RoutePlanner.Application.Planning;
using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Tests.Integration.Support;
using Xunit;

namespace VR.RoutePlanner.Tests.Integration.Planning;

/// <summary>
/// Маршрут на реальной карте 801: допустимость ребер и наличие строк ресурсов.
/// </summary>
[Collection(VrtWriteCollection.Name)]
public sealed class RoutePlannerRealMapTests
{
    [SkippableFact]
    public async Task PlanAsync_RealMap_RouteUsesOnlyPassableEdges()
    {
        var fixture = await PlanningTestFixture.CreateAsync(CancellationToken.None);
        var (_, _, result) = await fixture.FirstRouteAsync(CancellationToken.None);

        Assert.True(result.Found, "маршрут между узлами карты 801 не найден");
        Assert.NotNull(result.Route);
        Assert.NotEmpty(result.Route.Segments);

        var rows = await fixture.Passability.GetAsync(
            fixture.MapVersionId,
            PlanningTestFixture.Profile,
            CancellationToken.None);
        var passable = rows.Where(row => row.IsPassable).Select(row => row.EdgeId).ToHashSet();

        foreach (var segment in result.Route.Segments)
        {
            Assert.True(
                fixture.EdgesByCode.TryGetValue(segment.EdgeCode, out var edge),
                $"ребра {segment.EdgeCode} нет в графе карты");

            Assert.Contains(edge!.Id, passable);
            Assert.Equal(EdgeGeometry.LengthMeters(edge), segment.LengthMeters, 6);
        }
    }

    [SkippableFact]
    public async Task PlanAsync_RealMap_AllSegmentsHaveValidResource()
    {
        var fixture = await PlanningTestFixture.CreateAsync(CancellationToken.None);
        var (_, _, result) = await fixture.FirstRouteAsync(CancellationToken.None);

        Assert.True(result.Found, "маршрут между узлами карты 801 не найден");
        Assert.NotNull(result.Route);

        var resources = (await fixture.Resources.GetAsync(
                fixture.MapVersionId,
                PlanningTestFixture.Profile,
                CancellationToken.None))
            .ToDictionary(row => (row.EdgeId, row.Direction));
        var passability = (await fixture.Passability.GetAsync(
                fixture.MapVersionId,
                PlanningTestFixture.Profile,
                CancellationToken.None))
            .ToDictionary(row => row.EdgeId);
        var widths = (await fixture.Widths.ReadAsync(fixture.MapVersionId, CancellationToken.None))
            .Widths
            .ToDictionary(width => width.EdgeId);
        var profileWidth = fixture.Options.Value.AgvProfiles[PlanningTestFixture.Profile].WidthMeters;
        var floor = fixture.Options.Value.Cost.ClearanceFloorMeters;

        foreach (var segment in result.Route.Segments)
        {
            var edge = fixture.EdgesByCode[segment.EdgeCode];

            Assert.NotEqual(Guid.Empty, segment.TrafficResourceId);
            Assert.True(segment.Passable, $"сегмент {segment.EdgeCode} помечен непроходимым");
            Assert.True(
                resources.ContainsKey((edge.Id, DirectionName(segment.Direction))),
                $"ресурс ребра {segment.EdgeCode} в направлении {segment.Direction} не найден в наборе профиля");

            Assert.True(
                passability.TryGetValue(edge.Id, out var row),
                $"у ребра {segment.EdgeCode} нет строки vrt_EdgePassability для профиля");
            Assert.True(row!.IsPassable, $"ребро {segment.EdgeCode} помечено непроходимым");

            Assert.True(
                widths.TryGetValue(edge.Id, out var width),
                $"у ребра {segment.EdgeCode} нет строки vrt_EdgeWidth");
            Assert.True(width!.WidthMeters > 0, $"ширина ребра {segment.EdgeCode} не положительна");
            Assert.Equal(
                Math.Max(width.WidthMeters - profileWidth, floor),
                segment.ClearanceMeters,
                6);
        }
    }

    [SkippableFact]
    public async Task PlanAsync_RealMap_SafetyRuleHolds()
    {
        var fixture = await PlanningTestFixture.CreateAsync(CancellationToken.None);
        var threshold = fixture.Options.Value.Cost.NarrowClearanceMeters;
        var profileWidth = fixture.Options.Value.AgvProfiles[PlanningTestFixture.Profile].WidthMeters;
        var floor = fixture.Options.Value.Cost.ClearanceFloorMeters;

        var passable = (await fixture.Passability.GetAsync(
                fixture.MapVersionId,
                PlanningTestFixture.Profile,
                CancellationToken.None))
            .Where(row => row.IsPassable)
            .Select(row => row.EdgeId)
            .ToHashSet();
        var widths = (await fixture.Widths.ReadAsync(fixture.MapVersionId, CancellationToken.None))
            .Widths
            .ToDictionary(width => width.EdgeId);

        var narrowEdges = fixture.EdgesByCode.Values
            .Where(edge => passable.Contains(edge.Id) && widths.ContainsKey(edge.Id))
            .Where(edge => Math.Max(widths[edge.Id].WidthMeters - profileWidth, floor) < threshold)
            .Take(10)
            .ToList();

        Skip.If(narrowEdges.Count == 0, $"на карте нет проходимых ребер с запасом меньше {threshold:F2} м");

        var planner = fixture.Planner;
        var risky = 0;
        var checkedRoutes = 0;

        foreach (var edge in narrowEdges)
        {
            var result = await planner.PlanAsync(
                new RoutePlanRequest(TestDatabase.MapId, PlanningTestFixture.Profile, edge.FromNodeId, edge.ToNodeId, false),
                CancellationToken.None);

            if (!result.Found || result.Route is null)
            {
                continue;
            }

            checkedRoutes++;
            var hasNarrow = result.Route.Segments.Any(segment => segment.IsNarrow);

            if (!hasNarrow)
            {
                Assert.False(
                    result.PredictedRisk,
                    $"риск выставлен без узких ребер: {edge.EdgeId}");
                continue;
            }

            risky++;
            Assert.True(
                result.PredictedRisk,
                $"в маршруте узкие ребра, но PredictedRisk = false: {edge.EdgeId}");
            Assert.True(
                result.Reason is RoutePlanReason.SafeDetourTooExpensive or RoutePlanReason.NoSafeDetour,
                $"узкий маршрут принят без причины, код: {result.Reason ?? "нет"}");
        }

        Assert.True(checkedRoutes > 0, "не нашлось ни одного маршрута для проверки правила безопасности");
        Assert.True(risky > 0, "ни один маршрут не прошел через узкое ребро, правило не проверено");
    }

    private static string DirectionName(RouteEdgeDirection direction) =>
        direction == RouteEdgeDirection.Forward ? "FORWARD" : "BACKWARD";
}
