using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Выбор целевого узла автопереезда: ближайший свободный узел нужного типа,
/// тай-брейк по NodeId (ordinal), три вида занятости.
/// </summary>
public sealed class RelocationTargetSelectorTests
{
    private const string Robot = "98001";

    private static readonly RelocationRobotPose RobotAtOrigin = new(Robot, null, 0, 0);

    [Fact]
    public void Select_NearestFreeChargingNode()
    {
        var nodes = new[] { Charging("ch1", 10, 0), Charging("ch2", 3, 0) };

        var selected = Select(nodes, RelocationNodeTypes.Charging, Occupancy());

        Assert.NotNull(selected);
        Assert.Equal("ch2", selected!.NodeId);
    }

    [Fact]
    public void Select_ParkingRequestsParkingNode()
    {
        var nodes = new[] { Charging("ch1", 1, 0), Parking("pa1", 5, 0) };

        var selected = Select(nodes, RelocationNodeTypes.Parking, Occupancy());

        Assert.NotNull(selected);
        Assert.Equal("pa1", selected!.NodeId);
        Assert.Equal(RelocationNodeTypes.Parking, selected.NodeType);
    }

    [Fact]
    public void Select_NodePhysicallyOccupiedByPose_IsExcluded()
    {
        var nodes = new[] { Charging("ch1", 1, 0), Charging("ch2", 5, 0) };
        var occupancy = Occupancy(poses: [new RelocationRobotPose("98002", null, 1.2, 0.2)]);

        var selected = Select(nodes, RelocationNodeTypes.Charging, occupancy);

        Assert.NotNull(selected);
        Assert.Equal("ch2", selected!.NodeId);
    }

    [Fact]
    public void Select_NodeOccupiedByForeignLastNodeId_IsExcluded()
    {
        var nodes = new[] { Charging("ch1", 1, 0), Charging("ch2", 5, 0) };
        var occupancy = Occupancy(poses: [new RelocationRobotPose("98002", "ch1", 40, 40)]);

        var selected = Select(nodes, RelocationNodeTypes.Charging, occupancy);

        Assert.NotNull(selected);
        Assert.Equal("ch2", selected!.NodeId);
    }

    [Fact]
    public void Select_NodeClaimedByActiveOrder_IsExcluded()
    {
        var nodes = new[] { Charging("ch1", 1, 0), Charging("ch2", 5, 0) };
        var occupancy = Occupancy(activeOrderTargets: Targets(("ch1", "98002")));

        var selected = Select(nodes, RelocationNodeTypes.Charging, occupancy);

        Assert.NotNull(selected);
        Assert.Equal("ch2", selected!.NodeId);
    }

    [Fact]
    public void Select_ReservedNode_IsExcluded()
    {
        var nodes = new[] { Charging("ch1", 1, 0), Charging("ch2", 5, 0) };
        var occupancy = Occupancy(reservations: Targets(("ch1", "98002")));

        var selected = Select(nodes, RelocationNodeTypes.Charging, occupancy);

        Assert.NotNull(selected);
        Assert.Equal("ch2", selected!.NodeId);
    }

    [Fact]
    public void Select_OwnPoseOrderAndReservation_DoNotBlock()
    {
        var nodes = new[] { Charging("ch1", 1, 0), Charging("ch2", 5, 0) };
        var occupancy = new RelocationOccupancy
        {
            Poses = [new RelocationRobotPose(Robot, "ch1", 1, 0)],
            ActiveOrderTargets = Targets(("ch1", Robot)),
            Reservations = Targets(("ch1", Robot)),
        };

        var selected = Select(nodes, RelocationNodeTypes.Charging, occupancy);

        Assert.NotNull(selected);
        Assert.Equal("ch1", selected!.NodeId);
    }

    [Fact]
    public void Select_AllNodesOccupied_ReturnsNull()
    {
        var nodes = new[] { Charging("ch1", 1, 0), Charging("ch2", 5, 0) };
        var occupancy = Occupancy(activeOrderTargets: Targets(("ch1", "98002"), ("ch2", "98003")));

        Assert.Null(Select(nodes, RelocationNodeTypes.Charging, occupancy));
    }

    [Fact]
    public void Select_EqualDistance_PrefersSmallestNodeIdOrdinal()
    {
        // Расстояние одинаковое, поэтому решает NodeId: в ordinal "ch10" < "ch9", потому что '1' < '9'.
        var nodes = new[] { Charging("ch9", 5, 0), Charging("ch10", -5, 0) };
        var occupancy = Occupancy();

        Assert.Equal("ch10", Select(nodes, RelocationNodeTypes.Charging, occupancy)!.NodeId);
        Assert.Equal(
            "ch10",
            Select(nodes.Reverse(), RelocationNodeTypes.Charging, occupancy)!.NodeId);
    }

    [Fact]
    public void Select_MiddleOccupied_ReturnsNextFreeNotNearestOccupied()
    {
        var nodes = new[] { Charging("ch1", 1, 0), Charging("ch2", 2, 0), Charging("ch3", 3, 0) };
        var occupancy = Occupancy(poses: [new RelocationRobotPose("98002", "ch2", 2, 0)]);

        var selected = Select(nodes, RelocationNodeTypes.Charging, occupancy);

        Assert.NotNull(selected);
        Assert.Equal("ch1", selected!.NodeId);
    }

    [Fact]
    public void Select_NoNodesOfRequestedType_ReturnsNull()
    {
        var nodes = new[] { Parking("pa1", 1, 0), Waypoint("ch1", 2, 0) };

        Assert.Null(Select(nodes, RelocationNodeTypes.Charging, Occupancy()));
    }

    [Fact]
    public void Select_WaypointWithChargingLikeCode_IsNotSelected()
    {
        var nodes = new[] { Waypoint("ch1", 1, 0), Charging("ch2", 9, 0) };

        var selected = Select(nodes, RelocationNodeTypes.Charging, Occupancy());

        Assert.NotNull(selected);
        Assert.Equal("ch2", selected!.NodeId);
    }

    [Fact]
    public void Select_EmptyMap_ReturnsNull()
    {
        Assert.Null(Select([], RelocationNodeTypes.Charging, Occupancy()));
    }

    private static WarehouseNode? Select(
        IEnumerable<WarehouseNode> nodes,
        string nodeType,
        RelocationOccupancy occupancy) =>
        RelocationTargetSelector.Select(nodes, nodeType, RobotAtOrigin, occupancy);

    private static RelocationOccupancy Occupancy(
        IReadOnlyCollection<RelocationRobotPose>? poses = null,
        IReadOnlyDictionary<string, string>? activeOrderTargets = null,
        IReadOnlyDictionary<string, string>? reservations = null) =>
        new()
        {
            Poses = poses ?? Array.Empty<RelocationRobotPose>(),
            ActiveOrderTargets = activeOrderTargets ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Reservations = reservations ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };

    private static IReadOnlyDictionary<string, string> Targets(params (string NodeId, string Serial)[] targets)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (nodeId, serial) in targets)
        {
            result[nodeId] = serial;
        }

        return result;
    }

    private static WarehouseNode Charging(string code, double x, double y) =>
        Node(code, RelocationNodeTypes.Charging, x, y);

    private static WarehouseNode Parking(string code, double x, double y) =>
        Node(code, RelocationNodeTypes.Parking, x, y);

    private static WarehouseNode Waypoint(string code, double x, double y) =>
        Node(code, "WAYPOINT", x, y);

    private static WarehouseNode Node(string code, string nodeType, double x, double y) =>
        new(
            Guid.Empty,
            code,
            Guid.Empty,
            x,
            y,
            0,
            0.1,
            0.1,
            nodeType,
            null,
            null,
            true);
}
