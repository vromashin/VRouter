namespace VR.RoutePlanner.Application.Configuration;

/// <summary>
/// Параметры функции стоимости маршрута из секции <c>Routing:Cost</c>.
/// Все числа задаются конфигурацией: в коде планировщика их нет.
/// </summary>
public sealed class CostOptions
{
    /// <summary>Имя вложенной секции конфигурации.</summary>
    public const string SectionName = "Cost";

    /// <summary>Базовая цена поворота на 90 градусов, секунды.</summary>
    public double TurnPenaltySeconds { get; set; } = 2.0;

    /// <summary>
    /// Штраф за проезд ребра в обратном направлении (задним ходом). Симулятор TUSKROBOTS
    /// плохо переносит длинный задний ход, поэтому маршруты с проездом «вперед» предпочтительнее.
    /// </summary>
    public double BackwardPenaltySeconds { get; set; } = 5.0;

    /// <summary>Вес штрафа за тесноту.</summary>
    public double ClearanceWeight { get; set; } = 5.0;

    /// <summary>Пол запаса в метрах: используется и как делитель штрафа за тесноту.</summary>
    public double ClearanceFloorMeters { get; set; } = 0.4;

    /// <summary>Вес штрафа за длину ребра, секунды на метр.</summary>
    public double LengthWeight { get; set; }

    /// <summary>Запас, ниже которого ребро считается узким, метры.</summary>
    public double NarrowClearanceMeters { get; set; } = 0.6;

    /// <summary>Запас, начиная с которого коридор считается безопасным, метры.</summary>
    public double SafeClearanceMeters { get; set; } = 1.0;

    /// <summary>Допустимое превышение цены безопасного объезда над ценой первого маршрута.</summary>
    public double SafetyOverrideRatio { get; set; } = 2.0;

    /// <summary>Множитель скорости при перевозке груза.</summary>
    public double LoadSpeedFactor { get; set; } = 0.7;

    /// <summary>Полосы скоростей по запасу: порог запаса и максимальная скорость.</summary>
    public List<SpeedBandOptions> SpeedBands { get; set; } = new()
    {
        new SpeedBandOptions { BelowClearanceMeters = 0.4, MaxSpeedMetersPerSecond = 0.3 },
        new SpeedBandOptions { BelowClearanceMeters = 0.7, MaxSpeedMetersPerSecond = 0.6 },
        new SpeedBandOptions { BelowClearanceMeters = 1.2, MaxSpeedMetersPerSecond = 1.2 },
    };

    /// <summary>Верхняя скорость профиля, метры в секунду; знаменатель эвристики.</summary>
    public double ProfileTopSpeedMetersPerSecond { get; set; } = 2.0;

    /// <summary>
    /// Проверяет параметры на старте сервиса.
    /// </summary>
    /// <returns>Текст ошибки либо <c>null</c>, если параметры корректны.</returns>
    public string? Validate()
    {
        if (TurnPenaltySeconds <= 0)
        {
            return Error(nameof(TurnPenaltySeconds), TurnPenaltySeconds);
        }

        if (ClearanceWeight <= 0)
        {
            return Error(nameof(ClearanceWeight), ClearanceWeight);
        }

        if (ClearanceFloorMeters <= 0)
        {
            return Error(nameof(ClearanceFloorMeters), ClearanceFloorMeters);
        }

        if (NarrowClearanceMeters <= 0)
        {
            return Error(nameof(NarrowClearanceMeters), NarrowClearanceMeters);
        }

        if (SafeClearanceMeters <= 0)
        {
            return Error(nameof(SafeClearanceMeters), SafeClearanceMeters);
        }

        if (LengthWeight < 0)
        {
            return $"{Prefix(nameof(LengthWeight))} не может быть отрицательным, получено {LengthWeight}.";
        }

        if (ClearanceFloorMeters >= NarrowClearanceMeters || NarrowClearanceMeters >= SafeClearanceMeters)
        {
            return $"{Prefix("")}должно выполняться ClearanceFloorMeters < NarrowClearanceMeters < SafeClearanceMeters, "
                + $"получено ClearanceFloorMeters={ClearanceFloorMeters}, "
                + $"NarrowClearanceMeters={NarrowClearanceMeters}, SafeClearanceMeters={SafeClearanceMeters}.";
        }

        if (SafetyOverrideRatio < 1)
        {
            return $"{Prefix(nameof(SafetyOverrideRatio))} должен быть не меньше 1, получено {SafetyOverrideRatio}.";
        }

        if (LoadSpeedFactor is < 0.1 or > 1.0)
        {
            return $"{Prefix(nameof(LoadSpeedFactor))} должен быть в диапазоне 0.1..1.0, получено {LoadSpeedFactor}.";
        }

        if (ProfileTopSpeedMetersPerSecond <= 0)
        {
            return Error(nameof(ProfileTopSpeedMetersPerSecond), ProfileTopSpeedMetersPerSecond);
        }

        return ValidateBands();
    }

    /// <summary>
    /// Скорость по запасу и признаку груза.
    /// </summary>
    /// <param name="clearanceMeters">Запас по ширине, метры.</param>
    /// <param name="hasLoad">Робот везет груз.</param>
    /// <returns>Скорость, метры в секунду.</returns>
    public double SpeedFor(double clearanceMeters, bool hasLoad)
    {
        var speed = ProfileTopSpeedMetersPerSecond;
        foreach (var band in SpeedBands)
        {
            if (clearanceMeters < band.BelowClearanceMeters)
            {
                speed = band.MaxSpeedMetersPerSecond;
                break;
            }
        }

        return hasLoad ? speed * LoadSpeedFactor : speed;
    }

    /// <summary>
    /// Цена поворота на заданный угол.
    /// </summary>
    /// <param name="turnDegrees">Угол между касательными, градусы; отрицательный угол считается нулевым.</param>
    /// <returns>Стоимость в секундах, не больше двух базовых штрафов.</returns>
    public double TurnCost(double turnDegrees)
    {
        if (turnDegrees <= 0)
        {
            return 0;
        }

        var cost = TurnPenaltySeconds * (turnDegrees / TurnNormalizationDegrees);
        var limit = MaxTurnPenalties * TurnPenaltySeconds;
        return cost > limit ? limit : cost;
    }

    /// <summary>Угол, к которому нормируется цена поворота: прямой угол.</summary>
    public const double TurnNormalizationDegrees = 90;

    /// <summary>Ограничение цены поворота в базовых штрафах.</summary>
    public const double MaxTurnPenalties = 2;

    private string? ValidateBands()
    {
        if (SpeedBands is null || SpeedBands.Count == 0)
        {
            return $"{Prefix(nameof(SpeedBands))} не может быть пустым: нужна хотя бы одна полоса скорости.";
        }

        var previousThreshold = 0.0;
        var previousSpeed = 0.0;
        foreach (var band in SpeedBands)
        {
            if (band is null)
            {
                return $"{Prefix(nameof(SpeedBands))} содержит пустую полосу.";
            }

            if (band.BelowClearanceMeters <= previousThreshold)
            {
                return $"{Prefix(nameof(SpeedBands))} должны строго возрастать по BelowClearanceMeters: "
                    + $"{previousThreshold} -> {band.BelowClearanceMeters}.";
            }

            if (band.MaxSpeedMetersPerSecond <= 0)
            {
                return Error($"{nameof(SpeedBands)}.MaxSpeedMetersPerSecond", band.MaxSpeedMetersPerSecond);
            }

            if (band.MaxSpeedMetersPerSecond <= previousSpeed)
            {
                return $"{Prefix(nameof(SpeedBands))} должны строго возрастать по MaxSpeedMetersPerSecond: "
                    + $"{previousSpeed} -> {band.MaxSpeedMetersPerSecond}.";
            }

            if (band.MaxSpeedMetersPerSecond > ProfileTopSpeedMetersPerSecond)
            {
                return $"{Prefix(nameof(SpeedBands))} не могут быть быстрее ProfileTopSpeedMetersPerSecond="
                    + $"{ProfileTopSpeedMetersPerSecond}, получено {band.MaxSpeedMetersPerSecond}.";
            }

            previousThreshold = band.BelowClearanceMeters;
            previousSpeed = band.MaxSpeedMetersPerSecond;
        }

        return null;
    }

    private static string Error(string name, double value) =>
        $"{Prefix(name)} должно быть больше нуля, получено {value}.";

    private static string Prefix(string name) => string.IsNullOrEmpty(name)
        ? $"Routing:{SectionName}: "
        : $"Routing:{SectionName}:{name}";
}

/// <summary>
/// Полоса скорости: при запасе ниже порога скорость не больше заданной.
/// </summary>
public sealed class SpeedBandOptions
{
    /// <summary>Верхняя граница запаса, метры: полоса действует при запасе меньше этого порога.</summary>
    public double BelowClearanceMeters { get; set; }

    /// <summary>Максимальная скорость в полосе, метры в секунду.</summary>
    public double MaxSpeedMetersPerSecond { get; set; }
}
