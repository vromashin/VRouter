namespace VR.RoutePlanner.Domain.Traffic;

/// <summary>
/// Связь ресурса с ребром графа в одном направлении обхода.
/// </summary>
/// <param name="TrafficResourceId">Идентификатор ресурса.</param>
/// <param name="EdgeId">Значение <c>public."Edges"."Id"</c>.</param>
/// <param name="Direction">Направление обхода: <c>FORWARD</c> или <c>BACKWARD</c>.</param>
/// <param name="Sequence">Порядковый номер ребра внутри ресурса, начиная с 1.</param>
public sealed record TrafficResourceEdge(
    Guid TrafficResourceId,
    Guid EdgeId,
    string Direction,
    int Sequence);
