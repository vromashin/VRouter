namespace VR.RoutePlanner.Domain.Traffic;

/// <summary>
/// Связь ресурса с узлом графа.
/// </summary>
/// <param name="TrafficResourceId">Идентификатор ресурса.</param>
/// <param name="NodeId">Значение <c>public."Nodes"."Id"</c>.</param>
/// <param name="AgvProfile">Имя профиля робота, для которого построен ресурс.</param>
public sealed record TrafficResourceNode(
    Guid TrafficResourceId,
    Guid NodeId,
    string AgvProfile);
