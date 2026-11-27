using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Действие <c>startCharging</c> в целевом узле <c>CHARGING</c> (Д-4). Перенесено из AGVControl
/// (<c>OrderService.cs:1746-1773</c>): точка зарядки смещена от узла по оси вил на половину корпуса
/// плюс 0.25 м, а <c>thetaWhileCharging</c> развернут на 180 градусов от курса узла, потому что
/// робот встает к станции задом.
/// </summary>
public static class ChargingNodeAction
{
    /// <summary>Тип действия зарядки в узле.</summary>
    public const string ActionType = "startCharging";

    /// <summary>Блокирующий тип действия: как в эталоне для погрузчика.</summary>
    public const string BlockingType = "SOFT";

    /// <summary>Тип мгновенного действия остановки заряда.</summary>
    public const string StopActionType = "stopCharging";

    /// <summary>Вынос точки зарядки от узла сверх половины корпуса, метры.</summary>
    private const double ChargerOffsetMeters = 0.25;

    /// <summary>
    /// Параметры действия <c>startCharging</c>: идентификатор устройства и точка стыковки.
    /// </summary>
    /// <param name="node">Целевой узел карты с типом <c>CHARGING</c>.</param>
    /// <param name="robotLengthMeters">Длина корпуса <c>Routing:AgvProfiles[...].LengthMeters</c>.</param>
    /// <param name="chargerId">Идентификатор зарядного устройства <c>Routing:Charging:ChargerId</c>.</param>
    /// <returns>Параметры действия по ключам VDA-заказа.</returns>
    public static IReadOnlyDictionary<string, object?> Parameters(
        WarehouseNode node,
        double robotLengthMeters,
        string chargerId)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(chargerId);

        var offset = (Math.Max(robotLengthMeters, 0) / 2) + ChargerOffsetMeters;
        var theta = -node.Theta;

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["chg_id"] = chargerId,
            ["chargerX"] = node.X + (offset * Math.Cos(theta)),
            ["chargerY"] = node.Y + (offset * Math.Sin(theta)),
            ["thetaWhileCharging"] = theta,
        };
    }
}
