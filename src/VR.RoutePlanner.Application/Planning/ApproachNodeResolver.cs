using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Application.Planning;

/// <summary>
/// Результат поиска точки подхода: узел, до которого строится маршрут вместо целевого.
/// </summary>
/// <param name="Id">Внутренний идентификатор узла подхода.</param>
/// <param name="Code">Внешний код узла, например <c>wa132</c>.</param>
/// <param name="DistanceMeters">Расстояние от целевого узла до узла подхода, метры.</param>
public sealed record ApproachResolution(Guid Id, string Code, double DistanceMeters);

/// <summary>
/// Ищет точку подхода для целевого узла, у которого нет проезжих ребер.
///
/// Типичный случай: узел внутри стеллажного слота уже ширины робота. Робот физически
/// не входит в слот, поэтому заказ планируется до ближайшего узла с проезжим ребром,
/// а слот остается точкой подъезда. Карта при этом не меняется.
/// </summary>
public static class ApproachNodeResolver
{
    /// <summary>
    /// Возвращает узел подхода либо <c>null</c>, если целевой узел уже имеет проезжее ребро
    /// либо в радиусе нет ни одного активного узла с проезжим ребром.
    /// </summary>
    /// <param name="graph">Граф версии карты.</param>
    /// <param name="passableNodeIds">Узлы, инцидентные хотя бы одному проезжему ребру.</param>
    /// <param name="targetNodeId">Целевой узел заказа.</param>
    /// <param name="radiusMeters">Радиус поиска, метры.</param>
    public static ApproachResolution? Resolve(
        WarehouseGraph graph,
        IReadOnlyCollection<Guid> passableNodeIds,
        Guid targetNodeId,
        double radiusMeters)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(passableNodeIds);

        if (!graph.Nodes.TryGetValue(targetNodeId, out var target))
        {
            return null;
        }

        var passable = passableNodeIds as ISet<Guid> ?? new HashSet<Guid>(passableNodeIds);
        if (passable.Contains(target.Id))
        {
            return null;
        }

        ApproachResolution? best = null;
        foreach (var node in graph.Nodes.Values)
        {
            if (!node.IsActive || !passable.Contains(node.Id))
            {
                continue;
            }

            var distance = Math.Sqrt(
                Math.Pow(node.X - target.X, 2) + Math.Pow(node.Y - target.Y, 2));

            if (distance > radiusMeters)
            {
                continue;
            }

            if (best is null
                || distance < best.DistanceMeters
                || (Math.Abs(distance - best.DistanceMeters) < 1e-9
                    && string.CompareOrdinal(node.NodeId, best.Code) < 0))
            {
                best = new ApproachResolution(node.Id, node.NodeId, distance);
            }
        }

        return best;
    }
}
