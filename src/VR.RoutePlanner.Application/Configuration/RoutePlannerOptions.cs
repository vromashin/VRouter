using Microsoft.Extensions.Options;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Application.Configuration;

/// <summary>
/// Параметры маршрутизации из секции конфигурации <c>Routing</c>.
/// </summary>
public sealed class RoutePlannerOptions
{
    /// <summary>Имя секции конфигурации.</summary>
    public const string SectionName = "Routing";

    /// <summary>Количество базовых узлов маршрута. Заполняется этапом 2.</summary>
    public int BaseNodesCount { get; set; } = 40;

    /// <summary>Горизонт резервирования в секундах. Заполняется этапом 3.</summary>
    public double ReservationHorizonSeconds { get; set; } = 30;

    /// <summary>Горизонт освобождения в узлах. Заполняется этапом 3.</summary>
    public int ReleaseHorizonNodes { get; set; } = 15;

    /// <summary>Горизонт освобождения в секундах. Заполняется этапом 3.</summary>
    public double ReleaseHorizonSeconds { get; set; } = 60;

    /// <summary>
    /// Профили роботов: ключ это <c>Name</c>, значение это габариты корпуса.
    /// Список не пуст, имена уникальны без учета регистра.
    /// </summary>
    public Dictionary<string, AgvProfileOptions> AgvProfiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Имя профиля по умолчанию из <see cref="AgvProfiles"/>.</summary>
    public string DefaultAgvProfile { get; set; } = "tusk-forklift-1102";

    /// <summary>Боковой зазор S в метрах между роботами в коридоре.</summary>
    public double SideMargin { get; set; } = 0.2;

    /// <summary>
    /// Физический минимум зазора с каждой стороны, ниже которого проезд запрещён.
    /// Отделяет «робот не влезает» (непроходимо) от «влезает, но тесно» (проходимо медленно).
    /// </summary>
    public double MinPhysicalClearanceMeters { get; set; } = 0.1;

    /// <summary>
    /// Учитывать ширину коридора при определении проходимости. Как в старом AGVControl
    /// по умолчанию выключено: маршрут строится по любым ребрам, а ширина влияет только
    /// на стоимость, скорость и класс коридора.
    /// </summary>
    public bool RequirePhysicalWidth { get; set; }

    /// <summary>
    /// Радиус поиска точки подхода, метры. Если у целевого узла нет проезжих ребер
    /// (слот уже робота), маршрут строится до ближайшего узла с проезжим ребром в этом радиусе.
    /// </summary>
    public double ApproachRadiusMeters { get; set; } = 25.0;

    /// <summary>
    /// Радиус инфляции растра в метрах: ширина коридора W = 2 * (d + InflationRadius).
    /// Обязателен и не зависит от габаритов робота: определяется растром (например H80).
    /// </summary>
    public double InflationRadius { get; set; } = 0.4;

    /// <summary>Ширина коридора по умолчанию, если траектория ребра недоступна.</summary>
    public double DefaultCorridorWidth { get; set; } = 2.0;

    /// <summary>Ширина коридора для режима <see cref="WidthSource.Manual"/>.</summary>
    public double ManualCorridorWidth { get; set; } = 2.0;

    /// <summary>Ширины коридоров по значению поля <c>Edges.Corridor</c> для режима <see cref="WidthSource.Manual"/>.</summary>
    public Dictionary<string, double> ManualCorridorWidths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Источник ширины коридора.</summary>
    public WidthSource WidthSource { get; set; } = WidthSource.Raster;

    /// <summary>Каталог растров карт. Имя файла берется из <c>Maps.ImageUrl</c>.</summary>
    public string? MapRasterPath { get; set; } = "/var/data/rms-map-files";

    /// <summary>Запасной HTTP-адрес растров, используется только если <see cref="MapRasterPath"/> пуст.</summary>
    public string? MapRasterUrl { get; set; } = "http://localhost:5247/map-files";

    /// <summary>Порог препятствия на растре, 0..255.</summary>
    public int ObstacleThreshold { get; set; } = 128;

    /// <summary>Шаг дискретизации траектории ребра в метрах.</summary>
    public double TrajectorySampleStep { get; set; } = 0.05;

    /// <summary>Пороги классов коридоров по значению Nmax.</summary>
    public CorridorClassThresholds CorridorClassThresholds { get; set; } = new();

    /// <summary>Параметры функции стоимости маршрута из секции <c>Routing:Cost</c>.</summary>
    public CostOptions Cost { get; set; } = new();

    /// <summary>Параметры отправки заказов из секции <c>Routing:Order</c>.</summary>
    public OrderOptions Order { get; set; } = new();

    /// <summary>
    /// Проверяет параметры на старте сервиса.
    /// </summary>
    /// <returns>Текст ошибки либо <c>null</c>, если параметры корректны.</returns>
    public string? Validate()
    {
        var profileError = ValidateProfiles();
        if (profileError is not null)
        {
            return profileError;
        }

        if (SideMargin < 0)
        {
            return $"Routing:SideMargin не может быть отрицательным, получено {SideMargin}.";
        }

        if (ApproachRadiusMeters <= 0)
        {
            return $"Routing:ApproachRadiusMeters должен быть больше нуля, задано {ApproachRadiusMeters}.";
        }

        if (MinPhysicalClearanceMeters <= 0 || MinPhysicalClearanceMeters >= SideMargin)        {
            return $"Routing:MinPhysicalClearanceMeters должен быть больше нуля и меньше SideMargin, получено {MinPhysicalClearanceMeters}.";
        }

        if (InflationRadius <= 0)
        {
            return $"Routing:InflationRadius должен быть больше нуля, получено {InflationRadius}.";
        }

        if (DefaultCorridorWidth <= 0)
        {
            return $"Routing:DefaultCorridorWidth должен быть больше нуля, получено {DefaultCorridorWidth}.";
        }

        if (ManualCorridorWidth <= 0)
        {
            return $"Routing:ManualCorridorWidth должен быть больше нуля, получено {ManualCorridorWidth}.";
        }

        if (TrajectorySampleStep <= 0)
        {
            return $"Routing:TrajectorySampleStep должен быть больше нуля, получено {TrajectorySampleStep}.";
        }

        if (ObstacleThreshold is < 0 or > 255)
        {
            return $"Routing:ObstacleThreshold должен быть в диапазоне 0..255, получено {ObstacleThreshold}.";
        }

        var thresholds = CorridorClassThresholds;
        if (thresholds is null)
        {
            return "Routing:CorridorClassThresholds не задан.";
        }

        if (Cost is null)
        {
            return "Routing:Cost не задан.";
        }

        var costError = Cost.Validate();
        if (costError is not null)
        {
            return costError;
        }

        var orderError = Order.Validate();
        if (orderError is not null)
        {
            return orderError;
        }

        var values = new[] { thresholds.C1, thresholds.C2, thresholds.C3, thresholds.C4, thresholds.C5 };
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] <= values[i - 1])
            {
                return "Routing:CorridorClassThresholds должны строго возрастать: "
                    + $"C1={thresholds.C1}, C2={thresholds.C2}, C3={thresholds.C3}, C4={thresholds.C4}, C5={thresholds.C5}.";
            }
        }

        return null;
    }

    private string? ValidateProfiles()
    {
        if (AgvProfiles is null || AgvProfiles.Count == 0)
        {
            return "Routing:AgvProfiles не может быть пустым: нужен хотя бы один профиль робота.";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in AgvProfiles)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                return "Routing:AgvProfiles содержит профиль с пустым именем.";
            }

            if (!seen.Add(pair.Key))
            {
                return $"Routing:AgvProfiles содержит повторяющееся имя профиля '{pair.Key}'.";
            }

            var profile = pair.Value;
            if (profile is null)
            {
                return $"Routing:AgvProfiles['{pair.Key}'] не задан.";
            }

            if (profile.WidthMeters <= 0)
            {
                return $"Routing:AgvProfiles['{pair.Key}'].WidthMeters должен быть больше нуля, получено {profile.WidthMeters}.";
            }

            if (profile.LengthMeters <= 0)
            {
                return $"Routing:AgvProfiles['{pair.Key}'].LengthMeters должен быть больше нуля, получено {profile.LengthMeters}.";
            }

            if (profile.HeightMeters <= 0)
            {
                return $"Routing:AgvProfiles['{pair.Key}'].HeightMeters должен быть больше нуля, получено {profile.HeightMeters}.";
            }

            if (profile.LengthMeters < profile.WidthMeters)
            {
                return $"Routing:AgvProfiles['{pair.Key}'].LengthMeters не может быть меньше WidthMeters, "
                    + $"получено LengthMeters={profile.LengthMeters}, WidthMeters={profile.WidthMeters}.";
            }
        }

        var defaultName = DefaultAgvProfile;
        if (!string.IsNullOrWhiteSpace(defaultName) && !AgvProfiles.ContainsKey(defaultName))
        {
            return $"Routing:DefaultAgvProfile = '{defaultName}' отсутствует в Routing:AgvProfiles.";
        }

        return null;
    }
}

/// <summary>
/// Пороги классов коридоров по значению Nmax. Значения должны строго возрастать.
/// </summary>
public sealed class CorridorClassThresholds
{
    /// <summary>Верхняя граница Nmax для класса C1.</summary>
    public int C1 { get; set; } = 1;

    /// <summary>Верхняя граница Nmax для класса C2.</summary>
    public int C2 { get; set; } = 2;

    /// <summary>Верхняя граница Nmax для класса C3.</summary>
    public int C3 { get; set; } = 3;

    /// <summary>Верхняя граница Nmax для класса C4.</summary>
    public int C4 { get; set; } = 4;

    /// <summary>Нижняя граница Nmax для класса C5.</summary>
    public int C5 { get; set; } = 5;
}

/// <summary>
/// Габариты профиля робота из конфигурации <c>Routing:AgvProfiles</c>.
/// </summary>
public sealed class AgvProfileOptions
{
    /// <summary>Имя профиля: совпадает с ключом словаря и попадает в код ресурса.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Ширина корпуса B в метрах.</summary>
    public double WidthMeters { get; set; }

    /// <summary>Длина корпуса в метрах.</summary>
    public double LengthMeters { get; set; }

    /// <summary>Высота корпуса в метрах.</summary>
    public double HeightMeters { get; set; }
}

/// <summary>
/// Проверка <see cref="RoutePlannerOptions"/> при старте сервиса.
/// </summary>
public sealed class RoutePlannerOptionsValidator : IValidateOptions<RoutePlannerOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RoutePlannerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var error = options.Validate();
        return error is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error);
    }
}
