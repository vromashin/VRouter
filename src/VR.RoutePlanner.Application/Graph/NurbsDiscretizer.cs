using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Application.Graph;

/// <summary>
/// Оценка и дискретизация NURBS-кривой траектории ребра методом de Boor с учетом весов.
/// Реализация собственная, внешние зависимости не используются.
/// </summary>
public static class NurbsDiscretizer
{
    /// <summary>Минимальное число отрезков при дискретизации кривой степени выше первой.</summary>
    private const int MinSegments = 2;

    /// <summary>Ограничение на число отрезков, защита от вырожденных входных данных.</summary>
    private const int MaxSegments = 200_000;

    /// <summary>
    /// Дискретизирует кривую в полилинию с шагом не грубее <paramref name="step"/> метров.
    /// </summary>
    /// <param name="curve">Кривая траектории, может быть <c>null</c>.</param>
    /// <param name="step">Максимальная длина отрезка полилинии, метры.</param>
    /// <returns>Полилиния; пустой список, если кривая отсутствует или некорректна.</returns>
    public static IReadOnlyList<MapPoint> Discretize(TrajectoryCurve? curve, double step)
    {
        if (curve is null || curve.Validate() is not null)
        {
            return Array.Empty<MapPoint>();
        }

        var points = curve.ControlPoints;

        if (step <= 0 || double.IsNaN(step) || double.IsInfinity(step))
        {
            step = 0.05;
        }

        if (curve.Degree == 1)
        {
            // Для степени 1 кривая это ломаная по всем контрольным точкам: промежуточные
            // контрольные точки не отбрасываются, отрезки длиннее шага дополнительно делятся.
            var linePoints = new List<MapPoint> { new(points[0].X, points[0].Y, null) };
            for (var i = 1; i < points.Count; i++)
            {
                AppendSegment(linePoints, points[i - 1], points[i], step);
            }

            return linePoints;
        }

        var chord = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            chord += Math.Sqrt(
                ((points[i].X - points[i - 1].X) * (points[i].X - points[i - 1].X)) +
                ((points[i].Y - points[i - 1].Y) * (points[i].Y - points[i - 1].Y)));
        }

        var segments = Math.Clamp((int)Math.Ceiling(chord / step), MinSegments, MaxSegments);
        var polyline = Sample(curve, segments);

        // Шаг контролируется по фактическим отрезкам: кривая может быть длиннее хорды контрольного многоугольника.
        while (segments < MaxSegments && MaxSegmentLength(polyline) > step)
        {
            segments = Math.Min(segments * 2, MaxSegments);
            polyline = Sample(curve, segments);
        }

        return polyline;
    }

    /// <summary>
    /// Считает точку кривой для параметра <paramref name="u"/> из диапазона вектора узлов.
    /// </summary>
    /// <param name="curve">Корректная кривая.</param>
    /// <param name="u">Параметр в диапазоне <c>[KnotVector[Degree], KnotVector[ControlPoints.Count]]</c>.</param>
    /// <returns>Точка кривой.</returns>
    public static MapPoint Evaluate(TrajectoryCurve curve, double u)
    {
        ArgumentNullException.ThrowIfNull(curve);

        var degree = curve.Degree;
        var knots = curve.KnotVector;
        var control = curve.ControlPoints;
        var lastControl = control.Count - 1;

        var low = knots[degree];
        var high = knots[control.Count];
        if (u < low)
        {
            u = low;
        }

        if (u > high)
        {
            u = high;
        }

        var span = FindSpan(degree, u, lastControl, knots);

        // Однородные координаты: (w * x, w * y, w).
        var dx = new double[degree + 1];
        var dy = new double[degree + 1];
        var dw = new double[degree + 1];

        for (var j = 0; j <= degree; j++)
        {
            var point = control[span - degree + j];
            dx[j] = point.X * point.Weight;
            dy[j] = point.Y * point.Weight;
            dw[j] = point.Weight;
        }

        for (var r = 1; r <= degree; r++)
        {
            for (var j = degree; j >= r; j--)
            {
                var denominator = knots[j + 1 + span - r] - knots[j + span - degree];
                var alpha = denominator == 0 ? 0 : (u - knots[j + span - degree]) / denominator;
                dx[j] = ((1 - alpha) * dx[j - 1]) + (alpha * dx[j]);
                dy[j] = ((1 - alpha) * dy[j - 1]) + (alpha * dy[j]);
                dw[j] = ((1 - alpha) * dw[j - 1]) + (alpha * dw[j]);
            }
        }

        var weight = dw[degree];
        if (weight == 0)
        {
            return new MapPoint(dx[degree], dy[degree], null);
        }

        return new MapPoint(dx[degree] / weight, dy[degree] / weight, null);
    }

    /// <summary>
    /// Считает длину полилинии в метрах.
    /// </summary>
    /// <param name="points">Точки полилинии.</param>
    /// <returns>Сумма длин отрезков.</returns>
    public static double PolylineLength(IReadOnlyList<MapPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var length = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            var dx = points[i].X - points[i - 1].X;
            var dy = points[i].Y - points[i - 1].Y;
            length += Math.Sqrt((dx * dx) + (dy * dy));
        }

        return length;
    }

    private static void AppendSegment(
        List<MapPoint> target,
        ControlPoint start,
        ControlPoint end,
        double step)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        var parts = Math.Max(1, (int)Math.Ceiling(length / step));

        for (var i = 1; i <= parts; i++)
        {
            if (i == parts)
            {
                target.Add(new MapPoint(end.X, end.Y, null));
                continue;
            }

            var t = (double)i / parts;
            target.Add(new MapPoint(start.X + (dx * t), start.Y + (dy * t), null));
        }
    }

    private static IReadOnlyList<MapPoint> Sample(TrajectoryCurve curve, int segments)
    {
        var result = new MapPoint[segments + 1];
        for (var i = 0; i <= segments; i++)
        {
            var u = curve.KnotVector[curve.Degree] +
                    ((curve.KnotVector[curve.ControlPoints.Count] - curve.KnotVector[curve.Degree]) * i / segments);
            result[i] = Evaluate(curve, u);
        }

        return result;
    }

    private static int FindSpan(int degree, double u, int lastControl, IReadOnlyList<double> knots)
    {
        if (u >= knots[lastControl + 1])
        {
            return lastControl;
        }

        if (u <= knots[degree])
        {
            return degree;
        }

        var low = degree;
        var high = lastControl + 1;
        var middle = (low + high) / 2;

        while (u < knots[middle] || u >= knots[middle + 1])
        {
            if (u < knots[middle])
            {
                high = middle;
            }
            else
            {
                low = middle;
            }

            middle = (low + high) / 2;
        }

        return middle;
    }

    private static double MaxSegmentLength(IReadOnlyList<MapPoint> points)
    {
        var max = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            var dx = points[i].X - points[i - 1].X;
            var dy = points[i].Y - points[i - 1].Y;
            max = Math.Max(max, Math.Sqrt((dx * dx) + (dy * dy)));
        }

        return max;
    }
}
