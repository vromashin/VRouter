using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Domain.Traffic;
using VR.RoutePlanner.Tests.Unit.Planning;
using Xunit;

namespace VR.RoutePlanner.Tests.Unit.Traffic;

/// <summary>
/// Реестр резервов: эксклюзив, идемпотентность, емкость, направление, атомарность набора,
/// что не резервируется, TTL и грейс после проезда.
/// </summary>
public sealed class TrafficReservationRegistryTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryAcquire_ExclusiveReservedByOther_IsBlocked()
    {
        var registry = Create();
        var resource = Guid.NewGuid();

        Assert.True(registry.TryAcquire("98001", TrafficFixtures.MapVersionId, [Entry(resource)], Start).Accepted);

        var result = registry.TryAcquire("98002", TrafficFixtures.MapVersionId, [Entry(resource)], Start);

        Assert.False(result.Accepted);
        Assert.Equal(TrafficReservationResult.ReasonBlocked, result.ReasonCode);
        Assert.Equal("98001", result.BlockedBySerial);
        Assert.Equal(resource, result.BlockedResourceId);
        Assert.Empty(registry.GetOwnerReservations("98002"));
    }

    [Fact]
    public void TryAcquire_SameOwnerTwice_IsIdempotent()
    {
        var registry = Create();
        var resource = Guid.NewGuid();

        registry.TryAcquire("98001", TrafficFixtures.MapVersionId, [Entry(resource)], Start);
        var later = Start.AddSeconds(5);
        var result = registry.TryAcquire("98001", TrafficFixtures.MapVersionId, [Entry(resource)], later);

        Assert.True(result.Accepted);
        var reservations = registry.GetOwnerReservations("98001");
        Assert.Single(reservations);
        Assert.Equal(later, reservations[0].RenewedAt);
        Assert.Equal(Start, reservations[0].AcquiredAt);
    }

    [Fact]
    public void Release_ByAnotherRobot_KeepsOwnerReservations()
    {
        var registry = Create();
        var resource = Guid.NewGuid();
        registry.TryAcquire("98001", TrafficFixtures.MapVersionId, [Entry(resource)], Start);

        Assert.Equal(0, registry.Release("98002"));
        Assert.Single(registry.GetOwnerReservations("98001"));

        Assert.Equal(1, registry.Release("98001"));
        Assert.Empty(registry.GetOwnerReservations("98001"));
    }

    [Fact]
    public void TryAcquire_ParallelResource_LimitedByCapacity()
    {
        var registry = Create();
        var shared = Entry(Guid.NewGuid(), TrafficResourceType.PARALLEL, capacity: 2);

        Assert.True(registry.TryAcquire("98003", TrafficFixtures.MapVersionId, [shared], Start).Accepted);
        var second = registry.TryAcquire("98001", TrafficFixtures.MapVersionId, [shared], Start);
        var third = registry.TryAcquire("98002", TrafficFixtures.MapVersionId, [shared], Start);

        Assert.True(second.Accepted);
        Assert.False(third.Accepted);
        Assert.Equal(TrafficReservationResult.ReasonBlocked, third.ReasonCode);
    }

    [Fact]
    public void TryAcquire_ParallelResource_OppositeDirectionIsBlocked()
    {
        var registry = Create();
        var resource = Guid.NewGuid();

        registry.TryAcquire(
            "98001",
            TrafficFixtures.MapVersionId,
            [Entry(resource, TrafficResourceType.PARALLEL, capacity: 4, direction: RouteEdgeDirection.Forward)],
            Start);

        var opposite = registry.TryAcquire(
            "98002",
            TrafficFixtures.MapVersionId,
            [Entry(resource, TrafficResourceType.PARALLEL, capacity: 4, direction: RouteEdgeDirection.Backward)],
            Start);

        Assert.False(opposite.Accepted);
        Assert.Equal("98001", opposite.BlockedBySerial);
    }

    [Fact]
    public void TryAcquire_DirectionalResource_AllowsOnlyFirstOwner()
    {
        var registry = Create();
        var resource = Guid.NewGuid();
        var forward = Entry(resource, TrafficResourceType.DIRECTIONAL, direction: RouteEdgeDirection.Forward);

        Assert.True(registry.TryAcquire("98001", TrafficFixtures.MapVersionId, [forward], Start).Accepted);

        // Встречное и попутное движение по DIRECTIONAL: владелец уже есть, второй не пускается.
        var sameDirection = registry.TryAcquire("98002", TrafficFixtures.MapVersionId, [forward], Start);
        Assert.False(sameDirection.Accepted);
    }

    [Fact]
    public void TryAcquire_ThirdResourceBlocked_AcquiresNothing()
    {
        var registry = Create();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        registry.TryAcquire("98009", TrafficFixtures.MapVersionId, [Entry(third)], Start);

        var result = registry.TryAcquire(
            "98001",
            TrafficFixtures.MapVersionId,
            [Entry(first), Entry(second), Entry(third)],
            Start);

        Assert.False(result.Accepted);
        Assert.Empty(registry.GetOwnerReservations("98001"));

        // Первые два ресурса остались свободными: набор не был захвачен частично.
        Assert.True(registry.TryAcquire("98002", TrafficFixtures.MapVersionId, [Entry(first), Entry(second)], Start).Accepted);
    }

    [Fact]
    public void TryAcquire_IndependentResource_IsNeverReserved()
    {
        var registry = Create();
        var resource = Guid.NewGuid();
        var entry = Entry(resource, TrafficResourceType.INDEPENDENT);

        Assert.True(registry.TryAcquire("98001", TrafficFixtures.MapVersionId, [entry], Start).Accepted);
        Assert.True(registry.TryAcquire("98002", TrafficFixtures.MapVersionId, [entry], Start).Accepted);
        Assert.Empty(registry.GetOwnerReservations("98001"));
    }

    [Fact]
    public void TryAcquire_NodeExclusiveNotReserved_ConflictZoneReserved()
    {
        var registry = Create();
        var nodeExclusive = Entry(Guid.NewGuid(), TrafficResourceType.EXCLUSIVE, isNode: true);
        var conflictZone = Entry(Guid.NewGuid(), TrafficResourceType.CONFLICT_ZONE, isNode: true);

        registry.TryAcquire("98001", TrafficFixtures.MapVersionId, [nodeExclusive, conflictZone], Start);

        var reservations = registry.GetOwnerReservations("98001");
        Assert.Single(reservations);
        Assert.Equal(conflictZone.ResourceId, reservations[0].ResourceId);
        Assert.True(registry.TryAcquire("98002", TrafficFixtures.MapVersionId, [nodeExclusive], Start).Accepted);
        Assert.False(registry.TryAcquire("98002", TrafficFixtures.MapVersionId, [conflictZone], Start).Accepted);
    }

    [Fact]
    public void Renew_LeavesHorizon_MarksPassed_AndSweepReleasesAfterGrace()
    {
        var registry = Create(reservationHorizonSeconds: 300, releaseHorizonSeconds: 60);
        var passed = Guid.NewGuid();
        var ahead = Guid.NewGuid();

        registry.TryAcquire(
            "98001",
            TrafficFixtures.MapVersionId,
            [Entry(passed, sequenceId: 0), Entry(ahead, sequenceId: 2)],
            Start);

        var renewed = registry.Renew("98001", TrafficFixtures.MapVersionId, [Entry(ahead, sequenceId: 4)], Start.AddSeconds(10));

        Assert.True(renewed.Accepted);
        var reservations = registry.GetOwnerReservations("98001");
        Assert.Equal(2, reservations.Count);
        Assert.NotNull(reservations.Single(item => item.ResourceId == passed).PassedAt);
        Assert.Null(reservations.Single(item => item.ResourceId == ahead).PassedAt);

        // Грейс еще не истек: проеханный ресурс держится.
        Assert.Empty(registry.Sweep(Start.AddSeconds(65)));
        Assert.Equal(2, registry.GetOwnerReservations("98001").Count);

        var removed = registry.Sweep(Start.AddSeconds(75));
        Assert.Single(removed);
        Assert.Equal(passed, removed[0].ResourceId);
        Assert.Single(registry.GetOwnerReservations("98001"));
    }

    [Fact]
    public void Sweep_ReservationTtlWithoutRenew_ReleasesEverything()
    {
        var registry = Create(reservationHorizonSeconds: 30);

        registry.TryAcquire("98001", TrafficFixtures.MapVersionId, [Entry(Guid.NewGuid())], Start);

        Assert.Empty(registry.Sweep(Start.AddSeconds(30)));
        Assert.Single(registry.GetOwnerReservations("98001"));

        Assert.Single(registry.Sweep(Start.AddSeconds(31)));
        Assert.Empty(registry.GetOwnerReservations("98001"));
    }

    [Fact]
    public void Renew_BlockedResource_KeepsAlreadyHeldAndReportsRefusal()
    {
        var registry = Create();
        var held = Guid.NewGuid();
        var busy = Guid.NewGuid();

        registry.TryAcquire("98001", TrafficFixtures.MapVersionId, [Entry(held)], Start);
        registry.TryAcquire("98009", TrafficFixtures.MapVersionId, [Entry(busy)], Start);

        var result = registry.Renew(
            "98001",
            TrafficFixtures.MapVersionId,
            [Entry(held), Entry(busy)],
            Start.AddSeconds(5));

        Assert.False(result.Accepted);
        Assert.Equal(TrafficReservationResult.ReasonBlocked, result.ReasonCode);
        Assert.Single(registry.GetOwnerReservations("98001"));
    }

    private static TrafficReservationRegistry Create(
        double reservationHorizonSeconds = 30,
        double releaseHorizonSeconds = 60) =>
        new(new FixedOptions<RoutePlannerOptions>(
            TrafficFixtures.Options(
                reservationHorizonSeconds: reservationHorizonSeconds,
                releaseHorizonSeconds: releaseHorizonSeconds)));

    private static TrafficHorizonEntry Entry(
        Guid resourceId,
        TrafficResourceType type = TrafficResourceType.EXCLUSIVE,
        int capacity = 1,
        RouteEdgeDirection? direction = null,
        bool isNode = false,
        int sequenceId = 0) =>
        TrafficFixtures.Entry(resourceId, type, capacity, direction, isNode, sequenceId);
}
