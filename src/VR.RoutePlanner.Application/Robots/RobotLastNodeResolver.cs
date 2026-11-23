using Microsoft.Extensions.Logging;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Узел, на котором стоит робот: <c>snapshot.LastNodeId</c> плюс граф карты.
/// Один резолв на всех: им пользуются <c>LegacyAgvTaskController</c> и диспетчер трафика,
/// чтобы не дублировать разбор.
/// </summary>
public interface IRobotLastNodeResolver
{
    /// <summary>
    /// Отдает узел последнего <c>lastNodeId</c> робота.
    /// </summary>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Узел либо <c>null</c>: состояние или узел неизвестны. Исключений не бросает.</returns>
    Task<WarehouseNode?> ResolveAsync(string serialNumber, CancellationToken ct);
}

/// <summary>
/// Резолв узла робота по последнему состоянию. В отличие от AGVControl
/// (<c>GetAgvLastNodeAsync</c>) не бросает на неизвестного робота и не отдает 404.
/// </summary>
public sealed class RobotLastNodeResolver : IRobotLastNodeResolver
{
    private readonly IRobotStateReader _states;
    private readonly IWarehouseGraphRepository _graphs;
    private readonly ILogger<RobotLastNodeResolver> _logger;

    /// <summary>
    /// Создает резолвер узла.
    /// </summary>
    /// <param name="states">Последние состояния роботов.</param>
    /// <param name="graphs">Графы карт.</param>
    /// <param name="logger">Журнал.</param>
    public RobotLastNodeResolver(
        IRobotStateReader states,
        IWarehouseGraphRepository graphs,
        ILogger<RobotLastNodeResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(graphs);
        ArgumentNullException.ThrowIfNull(logger);

        _states = states;
        _graphs = graphs;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WarehouseNode?> ResolveAsync(string serialNumber, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);

        var snapshot = await _states.GetLatestAsync(serialNumber, ct).ConfigureAwait(false);
        if (snapshot is null
            || string.IsNullOrWhiteSpace(snapshot.LastNodeId)
            || string.IsNullOrWhiteSpace(snapshot.MapId))
        {
            return null;
        }

        var graph = await _graphs.LoadAsync(snapshot.MapId!, ct).ConfigureAwait(false);
        var node = graph?.Nodes.Values
            .FirstOrDefault(item => string.Equals(item.NodeId, snapshot.LastNodeId, StringComparison.Ordinal));

        if (node is null)
        {
            _logger.LogWarning(
                "Робот {Serial}: узла {Node} нет на карте {Map}",
                serialNumber,
                snapshot.LastNodeId,
                snapshot.MapId);
        }

        return node;
    }
}
