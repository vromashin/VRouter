namespace VR.RoutePlanner.Domain.Graph;

/// <summary>
/// Загруженный в память граф одной карты.
/// </summary>
public sealed class WarehouseGraph
{
    /// <summary>Значение <c>Maps.Id</c>.</summary>
    public Guid MapKey { get; init; }

    /// <summary>Внешний идентификатор карты, например <c>801</c>.</summary>
    public string MapId { get; init; } = string.Empty;

    /// <summary>Версия карты из <c>Maps.MapVersion</c>.</summary>
    public string MapVersion { get; init; } = string.Empty;

    /// <summary>Версия графа: <c>MapVersion</c> плюс хеш отсортированных идентификаторов.</summary>
    public string GraphVersion { get; init; } = string.Empty;

    /// <summary>Узлы карты по внутреннему ключу.</summary>
    public IReadOnlyDictionary<Guid, WarehouseNode> Nodes { get; init; } = new Dictionary<Guid, WarehouseNode>();

    /// <summary>
    /// Ребра по ключу узла начала. Для двунаправленных ребер запись дополнительно
    /// присутствует под ключом узла конца, сам экземпляр ребра один и тот же.
    /// </summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<WarehouseEdge>> Edges { get; init; } =
        new Dictionary<Guid, IReadOnlyList<WarehouseEdge>>();
}
