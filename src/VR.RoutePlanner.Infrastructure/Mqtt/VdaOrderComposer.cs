using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;

namespace VR.RoutePlanner.Infrastructure.Mqtt;

/// <summary>
/// Сборка тела VDA-заказа через <see cref="VdaOrderBuilder"/>.
/// </summary>
public sealed class VdaOrderComposer : IVdaOrderComposer
{
    /// <inheritdoc />
    public string Compose(
        PlannedRoute route,
        WarehouseGraph graph,
        string mapId,
        string orderId,
        string manufacturer,
        string serialNumber,
        long orderNumber,
        long headerId,
        int releasedNodeCount,
        DateTimeOffset timestamp,
        double? firstNodeX = null,
        double? firstNodeY = null,
        double? firstNodeTheta = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        var order = VdaOrderBuilder.Build(
            route,
            graph,
            mapId,
            manufacturer,
            serialNumber,
            orderNumber,
            headerId,
            timestamp,
            firstNodeX,
            firstNodeY,
            firstNodeTheta,
            releasedNodeCount);

        order["orderId"] = orderId;
        return VdaOrderBuilder.Serialize(order);
    }
}
