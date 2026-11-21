using System.Diagnostics;
using VR.RoutePlanner.Application.Planning;
using VR.RoutePlanner.Tests.Integration.Support;
using Xunit;
using Xunit.Abstractions;

namespace VR.RoutePlanner.Tests.Integration.Planning;

/// <summary>
/// Бюджет планирования на реальной карте 801: сто планов между разными парами узлов.
/// </summary>
[Collection(VrtWriteCollection.Name)]
public sealed class RoutePlannerRealMapBudgetTests
{
    private const int PlanCount = 100;
    private const double BudgetMilliseconds = 200;

    private readonly ITestOutputHelper _output;

    public RoutePlannerRealMapBudgetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task PlanAsync_RealMap_HundredPlans_UnderBudget()
    {
        var fixture = await PlanningTestFixture.CreateAsync(CancellationToken.None);
        var nodes = fixture.Graph.Nodes.Values
            .Where(node => node.IsActive)
            .OrderBy(node => node.NodeId)
            .ToList();
        var plans = new List<long>(PlanCount);
        var found = 0;

        for (var offset = 1; offset < nodes.Count && found < PlanCount; offset += 3)
        {
            for (var index = 0; index + offset < nodes.Count && found < PlanCount; index++)
            {
                var from = nodes[index];
                var to = nodes[index + offset];
                var stopwatch = Stopwatch.StartNew();
                var result = await fixture.Planner.PlanAsync(
                    new RoutePlanRequest(TestDatabase.MapId, PlanningTestFixture.Profile, from.Id, to.Id, false),
                    CancellationToken.None);
                stopwatch.Stop();

                if (!result.Found)
                {
                    continue;
                }

                plans.Add(stopwatch.ElapsedMilliseconds);
                found++;
            }
        }

        Assert.Equal(PlanCount, found);

        var average = plans.Average();
        _output.WriteLine(
            $"планов: {plans.Count}, среднее {average:F1} мс, максимум {plans.Max()} мс, бюджет {BudgetMilliseconds} мс");

        Assert.True(
            average < BudgetMilliseconds,
            $"среднее время плана {average:F1} мс при бюджете {BudgetMilliseconds} мс");
    }
}
