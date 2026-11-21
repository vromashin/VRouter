namespace VR.RoutePlanner.Domain.Planning;

/// <summary>
/// Разложение цены сегмента для <c>?explain=true</c>.
/// </summary>
/// <param name="EdgeCode">Внешний код ребра.</param>
/// <param name="TurnDegrees">Угол поворота на входе в ребро, градусы.</param>
/// <param name="TravelSeconds">Длина, деленная на скорость.</param>
/// <param name="TurnSeconds">Стоимость поворота на входе в ребро.</param>
/// <param name="ClearanceSeconds">Штраф за тесноту: <c>ClearanceWeight * (ClearanceFloor / запас)^2</c>.</param>
/// <param name="LengthSeconds">Штраф за длину: <c>LengthWeight * длина</c>.</param>
/// <param name="WaitSeconds">Оценка ожидания на ребре.</param>
public sealed record SegmentExplanation(
    string EdgeCode,
    double TurnDegrees,
    double TravelSeconds,
    double TurnSeconds,
    double ClearanceSeconds,
    double LengthSeconds,
    double WaitSeconds);

/// <summary>
/// Разложение цены маршрута на составляющие.
/// </summary>
/// <param name="Segments">Разложение по сегментам.</param>
/// <param name="TotalTravelSeconds">Сумма времени проезда.</param>
/// <param name="TotalTurnSeconds">Сумма стоимостей поворотов.</param>
/// <param name="TotalClearanceSeconds">Сумма штрафов за тесноту.</param>
/// <param name="TotalLengthSeconds">Сумма штрафов за длину.</param>
/// <param name="TotalWaitSeconds">Сумма оценок ожидания.</param>
/// <param name="Formula">Текст формулы с подставленными коэффициентами.</param>
public sealed record RouteExplanation(
    IReadOnlyList<SegmentExplanation> Segments,
    double TotalTravelSeconds,
    double TotalTurnSeconds,
    double TotalClearanceSeconds,
    double TotalLengthSeconds,
    double TotalWaitSeconds,
    string Formula);
