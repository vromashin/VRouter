using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Planning;

namespace VR.RoutePlanner.Application.Planning;

/// <summary>
/// Одно прохождение ребра в выбранном направлении с посчитанной ценой.
/// </summary>
/// <param name="Edge">Ребро.</param>
/// <param name="Direction">Направление прохождения.</param>
/// <param name="FromNodeId">Узел входа.</param>
/// <param name="ToNodeId">Узел выхода.</param>
/// <param name="Breakdown">Компоненты цены без поворота и ожидания.</param>
/// <param name="WaitSeconds">Ожидание на ребре из <see cref="IWaitEstimator"/>.</param>
public sealed record EdgeTraversal(
    WarehouseEdge Edge,
    RouteEdgeDirection Direction,
    Guid FromNodeId,
    Guid ToNodeId,
    EdgeCostBreakdown Breakdown,
    double WaitSeconds)
{
    /// <summary>Цена прохождения без поворота на входе.</summary>
    public double BaseCost => Breakdown.TravelSeconds + Breakdown.ClearanceSeconds + Breakdown.LengthWeightSeconds;
}

/// <summary>
/// Состояние поиска: узел и ребро, по которому в него пришли.
/// </summary>
/// <param name="NodeId">Узел.</param>
/// <param name="IncomingEdgeId">Ребро входа либо <see cref="Guid.Empty"/> для старта.</param>
public readonly record struct SearchState(Guid NodeId, Guid IncomingEdgeId);

/// <summary>
/// Итог поиска: цепочка прохождений и полная цена.
/// </summary>
/// <param name="Path">Прохождения в порядке движения.</param>
/// <param name="Cost">Полная цена маршрута.</param>
public sealed record SearchOutcome(IReadOnlyList<EdgeTraversal> Path, double Cost);
