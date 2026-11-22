using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;

namespace VR.RoutePlanner.Application.Planning;

/// <summary>
/// Функция стоимости ребра: запас, скорость и компоненты цены из <see cref="CostOptions"/>.
/// </summary>
public sealed class EdgeCostCalculator
{
    private readonly CostOptions _cost;
    private readonly bool _respectRotationAllowed;
    /// <summary>
    /// Создает калькулятор по параметрам планировщика.
    /// </summary>
    public bool RespectRotationAllowed => _respectRotationAllowed;
    /// <param name="options">Параметры секции <c>Routing</c>.</param>
    public EdgeCostCalculator(IOptions<RoutePlannerOptions> options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).Value.Cost, options.Value.RespectRotationAllowed)
    {
    }

    /// <summary>
    /// Создает калькулятор по параметрам стоимости.
    /// </summary>
    /// <param name="cost">Параметры <c>Routing:Cost</c>.</param>
    /// <param name="respectRotationAllowed">Учитывать ли RotationAllowed при поворотах.</param>
    public EdgeCostCalculator(CostOptions cost, bool respectRotationAllowed = false)
    {
        ArgumentNullException.ThrowIfNull(cost);
        _respectRotationAllowed = respectRotationAllowed;
        _cost = cost;
    }

    /// <summary>Порог поворота в градусах: меньший угол поворотом не считается.</summary>
    public const double TurnThresholdDegrees = 5;

    /// <summary>Запас ширины с нижним ограничением.</summary>
    public double Clearance(double widthMeters, double profileWidthMeters) =>
        Math.Max(widthMeters - profileWidthMeters, _cost.ClearanceFloorMeters);

    /// <summary>Цена ребра без поворота и ожидания.</summary>
    public EdgeCostBreakdown Calculate(
        double lengthMeters,
        double widthMeters,
        double profileWidthMeters,
        bool hasLoad)
    {
        var clearance = Clearance(widthMeters, profileWidthMeters);
        var speed = _cost.SpeedFor(clearance, hasLoad);
        var ratio = _cost.ClearanceFloorMeters / clearance;
        var travel = lengthMeters / speed;
        var clearanceCost = _cost.ClearanceWeight * ratio * ratio;
        var lengthCost = _cost.LengthWeight * lengthMeters;

        return new EdgeCostBreakdown(lengthMeters, clearance, speed, travel, clearanceCost, lengthCost);
    }

    /// <summary>Цена поворота: ноль при угле не больше <see cref="TurnThresholdDegrees"/>.</summary>
    public double TurnCost(double turnDegrees) =>
        turnDegrees <= TurnThresholdDegrees ? 0 : _cost.TurnCost(turnDegrees);

    /// <summary>Считается ли ребро узким при данном запасе.</summary>
    public bool IsNarrow(double clearanceMeters) => clearanceMeters < _cost.NarrowClearanceMeters;

    /// <summary>Верхняя скорость профиля: знаменатель эвристики.</summary>
    public double TopSpeedMetersPerSecond => _cost.ProfileTopSpeedMetersPerSecond;

    /// <summary>Запас, ниже которого ребро узкое.</summary>
    public double NarrowClearanceMeters => _cost.NarrowClearanceMeters;

    /// <summary>Допустимое превышение цены безопасного объезда.</summary>
    public double SafetyOverrideRatio => _cost.SafetyOverrideRatio;
}
