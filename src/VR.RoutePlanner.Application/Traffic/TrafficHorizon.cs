using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Application.Traffic;

/// <summary>
/// Ресурс горизонта: то, что робот должен держать за собой от текущего узла.
/// </summary>
/// <param name="ResourceId">Ресурс <c>vrt_TrafficResource.Id</c>.</param>
/// <param name="ResourceCode">Код ресурса для журнала.</param>
/// <param name="ResourceType">Тип ресурса: правило допуска других владельцев.</param>
/// <param name="Capacity">Емкость из строки ресурса.</param>
/// <param name="Direction">Направление проезда ребра для <c>DIRECTIONAL</c>/<c>PARALLEL</c>.</param>
/// <param name="SequenceId">Номер узла или ребра плана, где ресурс впервые встретился.</param>
/// <param name="IsNode">Ресурс узловой (<c>NODE</c>/<c>CZ</c>), а не коридорный.</param>
public sealed record TrafficHorizonEntry(
    Guid ResourceId,
    string ResourceCode,
    TrafficResourceType ResourceType,
    int Capacity,
    RouteEdgeDirection? Direction,
    int SequenceId,
    bool IsNode);

/// <summary>
/// Горизонт резервирования: ресурсы впереди робота по сохраненному плану.
/// </summary>
/// <param name="Entries">Ресурсы горизонта в порядке плана, без дублей.</param>
/// <param name="MissingEdgeIds">Ребра плана, для которых ресурс не разрешился (кроме виртуальных).</param>
public sealed record TrafficHorizon(
    IReadOnlyList<TrafficHorizonEntry> Entries,
    IReadOnlyList<string> MissingEdgeIds)
{
    /// <summary>Пустой горизонт.</summary>
    public static TrafficHorizon Empty { get; } = new([], []);
}
