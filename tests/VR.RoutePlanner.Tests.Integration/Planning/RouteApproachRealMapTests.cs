using VR.RoutePlanner.Application.Planning;
using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Tests.Integration.Support;
using Xunit;

namespace VR.RoutePlanner.Tests.Integration.Planning;

/// <summary>
/// Точка подхода на реальной карте: слот рабочего места, у которого нет проезжих ребер.
/// </summary>
[Collection(VrtWriteCollection.Name)]
public sealed class RouteApproachRealMapTests
{
    [SkippableFact]
    public async Task PlanAsync_IsolatedSlot_PlansToApproachNode()
    {
        var fixture = await PlanningTestFixture.CreateAsync(CancellationToken.None);
        var from = fixture.Graph.Nodes.Values.First(node => node.NodeId == "wa128");
        var slot = fixture.Graph.Nodes.Values.FirstOrDefault(node => node.NodeId == "wo-4-9");

        if (slot is null)
        {
            return;
        }

        var planner = fixture.Planner;
        var result = await planner.PlanAsync(
            new RoutePlanRequest(
                "801",
                PlanningTestFixture.Profile,
                from.Id,
                slot.Id,
                HasLoad: false),
            CancellationToken.None);

        Assert.True(result.Found, $"слот {slot.NodeId} должен планироваться через точку подхода, причина {result.Reason}");
        Assert.NotNull(result.ApproachNodeId);
        Assert.NotEqual(slot.NodeId, result.ApproachNodeId);
        Assert.True(result.ApproachDistanceMeters > 0, "расстояние до точки подхода должно быть положительным");
    }
}
