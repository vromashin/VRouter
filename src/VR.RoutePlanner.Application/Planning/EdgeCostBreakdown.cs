namespace VR.RoutePlanner.Application.Planning;

/// <summary>
/// Разложение цены одного ребра.
/// </summary>
/// <param name="LengthMeters">Длина ребра по геометрии, метры.</param>
/// <param name="ClearanceMeters">Запас ширины, метры.</param>
/// <param name="SpeedMetersPerSecond">Скорость прохождения, м/с.</param>
/// <param name="TravelSeconds">Длина / скорость.</param>
/// <param name="ClearanceSeconds">Штраф за тесноту.</param>
/// <param name="LengthWeightSeconds">Штраф за длину.</param>
public sealed record EdgeCostBreakdown(
    double LengthMeters,
    double ClearanceMeters,
    double SpeedMetersPerSecond,
    double TravelSeconds,
    double ClearanceSeconds,
    double LengthWeightSeconds);
