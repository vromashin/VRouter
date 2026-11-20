using VR.RoutePlanner.Application.Configuration;

namespace VR.RoutePlanner.Tests.Unit.Support;

/// <summary>
/// Готовые профили роботов для тестов.
/// </summary>
public static class TestProfiles
{
    /// <summary>Профиль шириной 0.8 м: на нем держатся числовые ожидания большинства тестов.</summary>
    public const string Name = "test";

    /// <summary>Создает словарь с одним профилем <see cref="Name"/>.</summary>
    public static Dictionary<string, AgvProfileOptions> Create(double widthMeters = 0.8) => new(StringComparer.Ordinal)
    {
        [Name] = new AgvProfileOptions
        {
            Name = Name,
            WidthMeters = widthMeters,
            LengthMeters = 1.2,
            HeightMeters = 0.2,
        },
    };
}
