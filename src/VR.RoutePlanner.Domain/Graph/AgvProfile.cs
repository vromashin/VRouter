namespace VR.RoutePlanner.Domain.Graph;

/// <summary>
/// Профиль робота: габариты корпуса из конфигурации <c>Routing:AgvProfiles</c>.
/// Массы в профиле нет: <c>Agvs.Weight</c> это вес груза, а не снаряженная масса.
/// </summary>
/// <param name="Name">Уникальное имя профиля, например <c>tusk-forklift-1102</c>.</param>
/// <param name="WidthMeters">Ширина корпуса B в метрах.</param>
/// <param name="LengthMeters">Длина корпуса в метрах.</param>
/// <param name="HeightMeters">Высота корпуса в метрах.</param>
public sealed record AgvProfile(
    string Name,
    double WidthMeters,
    double LengthMeters,
    double HeightMeters)
{
    /// <summary>Радиус описанной окружности корпуса, метры. На этапе 1 не применяется.</summary>
    public double FootprintRadius =>
        Math.Sqrt(Math.Pow(LengthMeters / 2, 2) + Math.Pow(WidthMeters / 2, 2));
}
