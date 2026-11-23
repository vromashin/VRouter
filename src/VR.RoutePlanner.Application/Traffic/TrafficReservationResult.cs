namespace VR.RoutePlanner.Application.Traffic;

/// <summary>
/// Итог попытки резервирования или продления горизонта.
/// </summary>
/// <param name="Accepted">Ресурсы свободны либо уже принадлежат этому роботу.</param>
/// <param name="ReasonCode">Код причины отказа: <see cref="ReasonBlocked"/> или <see cref="ReasonNoResource"/>.</param>
/// <param name="BlockedBySerial">Робот, который держит мешающий ресурс.</param>
/// <param name="BlockedResourceId">Мешающий ресурс <c>vrt_TrafficResource.Id</c>.</param>
/// <param name="BlockedResourceCode">Код мешающего ресурса.</param>
public sealed record TrafficReservationResult(
    bool Accepted,
    string? ReasonCode = null,
    string? BlockedBySerial = null,
    Guid? BlockedResourceId = null,
    string? BlockedResourceCode = null)
{
    /// <summary>Код причины: ресурс занят другим роботом.</summary>
    public const string ReasonBlocked = "TRAFFIC_BLOCKED";

    /// <summary>Код причины: ресурсов трафика для карты и профиля нет.</summary>
    public const string ReasonNoResource = "TRAFFIC_NO_RESOURCE";

    /// <summary>Ресурсы свободны.</summary>
    /// <returns>Принятый итог.</returns>
    public static TrafficReservationResult Accept() => new(true);

    /// <summary>Ресурс занят другим роботом.</summary>
    /// <param name="blockedBySerial">Владелец мешающего ресурса.</param>
    /// <param name="resourceId">Мешающий ресурс.</param>
    /// <param name="resourceCode">Код мешающего ресурса.</param>
    /// <returns>Отказ с кодом <see cref="ReasonBlocked"/>.</returns>
    public static TrafficReservationResult Blocked(
        string blockedBySerial,
        Guid resourceId,
        string resourceCode) =>
        new(false, ReasonBlocked, blockedBySerial, resourceId, resourceCode);

    /// <summary>Ресурсов трафика нет: профиль робота не совпадает с <c>AgvProfile</c> либо ребро без ресурса.</summary>
    /// <param name="resourceCode">Код ребра или ресурса, который не разрешился.</param>
    /// <returns>Отказ с кодом <see cref="ReasonNoResource"/>.</returns>
    public static TrafficReservationResult NoResource(string? resourceCode = null) =>
        new(false, ReasonNoResource, null, null, resourceCode);
}
