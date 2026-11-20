namespace VR.RoutePlanner.Domain.Graph;

/// <summary>
/// Контрольная точка NURBS-кривой с весом.
/// </summary>
/// <param name="X">Координата X, метры.</param>
/// <param name="Y">Координата Y, метры.</param>
/// <param name="Weight">Вес точки, должен быть больше нуля.</param>
public sealed record ControlPoint(double X, double Y, double Weight);

/// <summary>
/// Траектория ребра в формате БД: NURBS-кривая с весами контрольных точек.
/// </summary>
/// <param name="Degree">Степень кривой, в данных карты 801 встречаются 1 и 3.</param>
/// <param name="KnotVector">Вектор узлов, неубывающий.</param>
/// <param name="ControlPoints">Контрольные точки с весами.</param>
/// <param name="Raw">Исходный JSON для диагностики, может быть <c>null</c>.</param>
public sealed record TrajectoryCurve(
    int Degree,
    IReadOnlyList<double> KnotVector,
    IReadOnlyList<ControlPoint> ControlPoints,
    string? Raw)
{
    /// <summary>
    /// Проверяет корректность кривой по правилам загрузки графа.
    /// </summary>
    /// <returns><c>null</c>, если кривая корректна, иначе текст ошибки на русском.</returns>
    public string? Validate()
    {
        if (Degree < 1)
        {
            return $"степень кривой {Degree} меньше 1";
        }

        if (KnotVector.Count != ControlPoints.Count + Degree + 1)
        {
            return $"длина knotVector {KnotVector.Count} не равна ControlPoints.Count + Degree + 1 = {ControlPoints.Count + Degree + 1}";
        }

        if (ControlPoints.Count < Degree + 1)
        {
            return $"контрольных точек {ControlPoints.Count} меньше Degree + 1 = {Degree + 1}";
        }

        for (var i = 1; i < KnotVector.Count; i++)
        {
            if (KnotVector[i] < KnotVector[i - 1])
            {
                return $"knotVector не является неубывающим на позиции {i}";
            }
        }

        for (var i = 0; i < ControlPoints.Count; i++)
        {
            var weight = ControlPoints[i].Weight;
            if (double.IsNaN(weight) || double.IsInfinity(weight) || weight <= 0)
            {
                return $"вес контрольной точки {i} равен {weight}, требуется больше 0";
            }

            if (double.IsNaN(ControlPoints[i].X) || double.IsNaN(ControlPoints[i].Y))
            {
                return $"координаты контрольной точки {i} не являются числом";
            }
        }

        var span = KnotVector[^1] - KnotVector[0];
        if (span <= 0)
        {
            return "диапазон knotVector пуст";
        }

        return null;
    }
}
