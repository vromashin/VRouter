using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Application.Traffic;

/// <summary>
/// Разрешение узлов и ребер плана в транспортные ресурсы <c>vrt_*</c>.
/// Читает <c>vrt_TrafficResource</c>, <c>vrt_TrafficResourceNode</c> и
/// <c>vrt_TrafficResourceEdge</c> для пары (версия карты, профиль) и кэширует результат
/// в памяти процесса.
/// </summary>
public interface ITrafficResourceLookup
{
    /// <summary>
    /// Отдает разрешение ресурсов для карты и профиля робота.
    /// </summary>
    /// <param name="mapId">Идентификатор карты из сообщения <c>state</c>, например <c>801</c>.</param>
    /// <param name="agvProfile">Профиль робота (<c>Routing:DefaultAgvProfile</c>).</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Разрешение ресурсов либо <c>null</c>, если карта, версия или ресурсы не найдены.</returns>
    Task<ITrafficResourceMap?> GetAsync(string mapId, string agvProfile, CancellationToken ct);
}

/// <summary>
/// Готовое разрешение ресурсов одной версии карты для одного профиля робота.
/// </summary>
public interface ITrafficResourceMap
{
    /// <summary>Версия карты <c>vrt_MapVersion.Id</c>: первая часть ключа резерва.</summary>
    Guid MapVersionId { get; }

    /// <summary>Профиль робота.</summary>
    string AgvProfile { get; }

    /// <summary>
    /// Ресурс узла по VDA-идентификатору.
    /// </summary>
    /// <param name="nodeId">Идентификатор узла VDA, например <c>wa152</c>.</param>
    /// <returns>Ресурс узла либо <c>null</c>.</returns>
    TrafficResourceRef? FindNode(string nodeId);

    /// <summary>
    /// Ресурс ребра по коду ребра и узлу, с которого робот на него входит.
    /// </summary>
    /// <param name="edgeId">Код ребра, например <c>801-wa152-wa167</c>.</param>
    /// <param name="startNodeId">Узел входа в ребро из плана: задает направление проезда.</param>
    /// <returns>Ресурс ребра с направлением либо <c>null</c>.</returns>
    TrafficEdgeResource? FindEdge(string edgeId, string? startNodeId);
}

/// <summary>
/// Ресурс узла.
/// </summary>
/// <param name="ResourceId">Ресурс <c>vrt_TrafficResource.Id</c>.</param>
/// <param name="ResourceCode">Код ресурса.</param>
/// <param name="ResourceType">Тип ресурса: <c>CONFLICT_ZONE</c> или узловой <c>EXCLUSIVE</c>.</param>
/// <param name="Capacity">Емкость ресурса.</param>
public sealed record TrafficResourceRef(
    Guid ResourceId,
    string ResourceCode,
    TrafficResourceType ResourceType,
    int Capacity);

/// <summary>
/// Ресурс ребра с направлением проезда.
/// </summary>
/// <param name="ResourceId">Ресурс <c>vrt_TrafficResource.Id</c>.</param>
/// <param name="ResourceCode">Код ресурса.</param>
/// <param name="ResourceType">Тип ресурса.</param>
/// <param name="Capacity">Емкость ресурса.</param>
/// <param name="Direction">Направление, в котором ребро входит в ресурс.</param>
public sealed record TrafficEdgeResource(
    Guid ResourceId,
    string ResourceCode,
    TrafficResourceType ResourceType,
    int Capacity,
    RouteEdgeDirection Direction);
