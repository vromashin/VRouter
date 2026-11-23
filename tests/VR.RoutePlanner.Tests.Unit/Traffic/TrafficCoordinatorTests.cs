using Microsoft.Extensions.Logging.Abstractions;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Tests.Unit.Planning;
using VR.RoutePlanner.Tests.Unit.Robots;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Traffic;

/// <summary>
/// Диспетчер трафика: добор горизонта, отсутствие дублей владельцев, грейс после проезда,
/// снятие терминальным статусом, свип и <c>TRAFFIC_NO_RESOURCE</c>.
/// </summary>
public sealed class TrafficCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OnState_AddsHorizon_AndSecondStateDoesNotDuplicateOwners()
    {
        var map = Map(nodeCount: 6, edgeCount: 5);
        var (coordinator, registry, time) = Create(map);

        var snapshot = TrafficFixtures.Snapshot(lastNodeSequenceId: 0);
        var first = await coordinator.OnStateAsync(snapshot, TrafficFixtures.Plan(6), CancellationToken.None);
        Assert.True(first.Accepted);

        time.Now = Start.AddSeconds(1);
        var second = await coordinator.OnStateAsync(snapshot, TrafficFixtures.Plan(6), CancellationToken.None);
        Assert.True(second.Accepted);

        var reservations = registry.GetOwnerReservations(snapshot.SerialNumber!);
        Assert.Equal(reservations.Count, reservations.Select(item => item.ResourceId).Distinct().Count());
        Assert.All(reservations, item => Assert.Equal(Start.AddSeconds(1), item.RenewedAt));
    }

    [Fact]
    public async Task OnState_PassedResource_GoesToGrace()
    {
        var map = Map(nodeCount: 6, edgeCount: 5);
        var (coordinator, registry, time) = Create(map);

        await coordinator.OnStateAsync(
            TrafficFixtures.Snapshot(lastNodeSequenceId: 0),
            TrafficFixtures.Plan(6),
            CancellationToken.None);

        time.Now = Start.AddSeconds(5);
        await coordinator.OnStateAsync(
            TrafficFixtures.Snapshot(lastNodeSequenceId: 6),
            TrafficFixtures.Plan(6),
            CancellationToken.None);

        var reservations = registry.GetOwnerReservations("98001");
        Assert.Contains(reservations, item => item.PassedAt is not null);
        Assert.Contains(reservations, item => item.PassedAt is null);
    }

    [Fact]
    public async Task Release_RemovesAllReservationsOfRobot()
    {
        var map = Map(nodeCount: 6, edgeCount: 5);
        var (coordinator, registry, _) = Create(map);

        await coordinator.OnStateAsync(
            TrafficFixtures.Snapshot(lastNodeSequenceId: 0),
            TrafficFixtures.Plan(6),
            CancellationToken.None);

        var released = await coordinator.ReleaseAsync("98001", CancellationToken.None);

        Assert.True(released > 0);
        Assert.Empty(registry.GetOwnerReservations("98001"));
    }

    [Fact]
    public async Task Sweep_ReleasesReservationsWithoutState()
    {
        var map = Map(nodeCount: 6, edgeCount: 5);
        var (coordinator, registry, time) = Create(map);

        await coordinator.OnStateAsync(
            TrafficFixtures.Snapshot(lastNodeSequenceId: 0),
            TrafficFixtures.Plan(6),
            CancellationToken.None);

        var swept = await coordinator.SweepAsync(time.Now.AddSeconds(31), CancellationToken.None);

        Assert.True(swept > 0);
        Assert.Empty(registry.GetOwnerReservations("98001"));
    }

    [Fact]
    public async Task OnState_WithoutResources_ReturnsNoResource()
    {
        var (coordinator, registry, _) = Create(map: null);

        var result = await coordinator.OnStateAsync(
            TrafficFixtures.Snapshot(lastNodeSequenceId: 0),
            TrafficFixtures.Plan(6),
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal(TrafficReservationResult.ReasonNoResource, result.ReasonCode);
        Assert.Empty(registry.GetOwnerReservations("98001"));
    }

    [Fact]
    public async Task TryReserveForOrder_OtherRobotHoldsCorridor_IsBlocked()
    {
        var map = Map(nodeCount: 6, edgeCount: 5);
        var (coordinator, _, _) = Create(map);

        Assert.True((await coordinator.OnStateAsync(
            TrafficFixtures.Snapshot("98001", lastNodeSequenceId: 0),
            TrafficFixtures.Plan(6),
            CancellationToken.None)).Accepted);

        var blocked = await coordinator.TryReserveForOrderAsync(
            TrafficFixtures.Snapshot("98002", lastNodeSequenceId: 0),
            TrafficFixtures.Plan(6),
            CancellationToken.None);

        Assert.False(blocked.Accepted);
        Assert.Equal(TrafficReservationResult.ReasonBlocked, blocked.ReasonCode);
        Assert.Equal("98001", blocked.BlockedBySerial);
    }

    private static (TrafficCoordinator Coordinator, TrafficReservationRegistry Registry, TestTimeProvider Time)
        Create(ITrafficResourceMap? map)
    {
        var options = new FixedOptions<Application.Configuration.RoutePlannerOptions>(TrafficFixtures.Options());
        var registry = new TrafficReservationRegistry(options);
        var time = new TestTimeProvider(Start);
        var coordinator = new TrafficCoordinator(
            new FakeTrafficResourceLookup(map),
            registry,
            options,
            time,
            NullLogger<TrafficCoordinator>.Instance);

        return (coordinator, registry, time);
    }

    private static FakeTrafficResourceMap Map(int nodeCount, int edgeCount)
    {
        var map = new FakeTrafficResourceMap();
        for (var index = 0; index < nodeCount; index++)
        {
            map.Node($"wa{index * 2}", Guid.NewGuid());
        }

        for (var index = 0; index < edgeCount; index++)
        {
            map.Edge($"801-e{index}", Guid.NewGuid(), fromNodeId: $"wa{index * 2}");
        }

        return map;
    }
}
