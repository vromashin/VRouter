using VR.RoutePlanner.Domain.Planning;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Application.Traffic;

/// <summary>
/// Резерв транспортного ресурса <c>vrt_TrafficResource.Id</c> за роботом.
/// Живет только в памяти процесса: в <c>vrt_*</c> резервы не пишутся.
/// </summary>
/// <param name="MapVersionId">Версия карты из <c>vrt_MapVersion.Id</c>: первая часть ключа резерва.</param>
/// <param name="ResourceId">Ресурс <c>vrt_TrafficResource.Id</c>: вторая часть ключа резерва.</param>
/// <param name="ResourceCode">Код ресурса для журнала, например <c>801-tusk-forklift-1102-C2-3</c>.</param>
/// <param name="OwnerSerial">Серийный номер робота-владельца.</param>
/// <param name="Direction">Направление проезда для коридоров <c>DIRECTIONAL</c>/<c>PARALLEL</c>.</param>
/// <param name="ResourceType">Тип ресурса: правило допуска других владельцев.</param>
/// <param name="Capacity">Емкость из строки ресурса.</param>
/// <param name="AcquiredAt">Момент захвата.</param>
/// <param name="RenewedAt">Момент последнего подтверждения по <c>state</c>.</param>
/// <param name="PassedAt">Момент, когда робот проехал ресурс: держится еще <c>ReleaseHorizonSeconds</c>.</param>
public sealed record TrafficReservation(
    Guid MapVersionId,
    Guid ResourceId,
    string ResourceCode,
    string OwnerSerial,
    RouteEdgeDirection? Direction,
    TrafficResourceType ResourceType,
    int Capacity,
    DateTimeOffset AcquiredAt,
    DateTimeOffset RenewedAt,
    DateTimeOffset? PassedAt);
