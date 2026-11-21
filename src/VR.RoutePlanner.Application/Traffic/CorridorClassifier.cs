using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Application.Traffic;

/// <summary>
/// Классификация коридора по ширине: Nmax = floor((W + S) / (B + S)) и маппинг Nmax в класс C1..C5.
/// </summary>
public static class CorridorClassifier
{
    /// <summary>
    /// Считает предельное число роботов в коридоре.
    /// </summary>
    /// <param name="corridorWidth">Ширина коридора W в метрах.</param>
    /// <param name="robotWidth">Ширина робота B в метрах.</param>
    /// <param name="sideMargin">Боковой зазор S в метрах.</param>
    /// <returns>Nmax, не меньше единицы.</returns>
    /// <exception cref="InvalidOperationException">B + S не положительны.</exception>
    public static int ComputeNmax(double corridorWidth, double robotWidth, double sideMargin)
    {
        var denominator = robotWidth + sideMargin;
        if (denominator <= 0)
        {
            throw new InvalidOperationException(
                $"RobotWidth + SideMargin должны быть больше нуля, получено {robotWidth} + {sideMargin}.");
        }

        var raw = Math.Floor((corridorWidth + sideMargin) / denominator);
        if (raw < 1)
        {
            return 1;
        }

        return raw >= int.MaxValue ? int.MaxValue : (int)raw;
    }

    /// <summary>
    /// Определяет класс коридора по значению Nmax. Пороги заданы конфигурацией и строго возрастают:
    /// класс C1 получает Nmax не больше C1, C2 не больше C2 и так далее, остальное попадает в C5.
    /// </summary>
    /// <summary>
    /// Класс коридора по значению <c>Nmax</c>.
    /// Правило: порог является включительной верхней границей класса, то есть класс это
    /// первый порог, который не меньше <paramref name="nmax"/>; значения выше <c>C5</c> остаются <c>C5</c>.
    /// Так пороги 1, 2, 3, 4, 5 дают C1 для <c>Nmax &lt;= 1</c>, C2 для <c>Nmax = 2</c> и так далее.
    /// </summary>
    /// <param name="nmax">Предельное число роботов в коридоре.</param>
    /// <param name="thresholds">Пороги классов.</param>
    /// <returns>Класс коридора.</returns>
    public static CorridorClass Classify(int nmax, CorridorClassThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        if (nmax <= thresholds.C1)
        {
            return CorridorClass.C1;
        }

        if (nmax <= thresholds.C2)
        {
            return CorridorClass.C2;
        }

        if (nmax <= thresholds.C3)
        {
            return CorridorClass.C3;
        }

        if (nmax <= thresholds.C4)
        {
            return CorridorClass.C4;
        }

        return CorridorClass.C5;
    }

    /// <summary>
    /// Проверяет проходимость коридора по физическому минимуму: <c>W >= B + 2 * C</c>,
    /// где C это минимальный зазор с каждой стороны, ниже которого робот не влезает.
    /// </summary>
    /// <param name="corridorWidth">Ширина коридора W в метрах.</param>
    /// <param name="profile">Профиль робота.</param>
    /// <param name="minClearance">Физический минимум зазора с каждой стороны в метрах.</param>
    /// <returns><c>true</c>, если робот профиля проходит коридор.</returns>
    public static bool IsPassable(double corridorWidth, AgvProfile profile, double minClearance)
    {
        ArgumentNullException.ThrowIfNull(profile);
        // Физический минимум: робот должен влезть с зазором minClearance с каждой стороны.
        // Желаемый запас (SideMargin) в проходимость не входит, он влияет на класс и скорость.
        return corridorWidth >= profile.WidthMeters + (2.0 * minClearance);
    }

    /// <summary>
    /// Тип транспортного ресурса по классу коридора.
    /// </summary>
    /// <param name="corridorClass">Класс коридора.</param>
    /// <returns>Тип ресурса.</returns>
    public static TrafficResourceType ResourceTypeOf(CorridorClass corridorClass) => corridorClass switch
    {
        CorridorClass.C1 => TrafficResourceType.EXCLUSIVE,
        CorridorClass.C2 => TrafficResourceType.DIRECTIONAL,
        CorridorClass.C3 => TrafficResourceType.PARALLEL,
        CorridorClass.C4 => TrafficResourceType.PARALLEL,
        CorridorClass.C5 => TrafficResourceType.INDEPENDENT,
        _ => throw new ArgumentOutOfRangeException(nameof(corridorClass), corridorClass, "Неизвестный класс коридора.")
    };
}
