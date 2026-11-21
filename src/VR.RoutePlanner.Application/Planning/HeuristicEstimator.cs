using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Application.Planning;

/// <summary>
/// Допустимая эвристика A*: евклидово расстояние до цели, деленное на верхнюю скорость профиля.
/// Скорость на ребре не бывает выше верхней скорости профиля, поэтому оценка не переоценивает
/// стоимость маршрута.
/// </summary>
public sealed class HeuristicEstimator
{
    private readonly IReadOnlyDictionary<Guid, WarehouseNode> _nodes;
    private readonly double _topSpeedMetersPerSecond;

    /// <summary>
    /// Создает оценку.
    /// </summary>
    /// <param name="nodes">Узлы карты.</param>
    /// <param name="topSpeedMetersPerSecond">Верхняя скорость профиля, метры в секунду.</param>
    public HeuristicEstimator(
        IReadOnlyDictionary<Guid, WarehouseNode> nodes,
        double topSpeedMetersPerSecond)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (topSpeedMetersPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(topSpeedMetersPerSecond),
                topSpeedMetersPerSecond,
                "Верхняя скорость должна быть больше нуля.");
        }

        _nodes = nodes;
        _topSpeedMetersPerSecond = topSpeedMetersPerSecond;
    }

    /// <summary>
    /// Оценка стоимости от узла до цели.
    /// </summary>
    /// <param name="fromNodeId">Текущий узел.</param>
    /// <param name="toNodeId">Цель.</param>
    /// <returns>Оценка в секундах, не меньше нуля.</returns>
    public double Estimate(Guid fromNodeId, Guid toNodeId)
    {
        if (!_nodes.TryGetValue(fromNodeId, out var from) || !_nodes.TryGetValue(toNodeId, out var to))
        {
            return 0;
        }

        var distance = EdgeGeometry.Distance(from, to);
        return distance / _topSpeedMetersPerSecond;
    }
}
