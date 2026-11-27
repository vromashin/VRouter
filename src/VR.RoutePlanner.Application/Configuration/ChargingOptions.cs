using Microsoft.Extensions.Options;

namespace VR.RoutePlanner.Application.Configuration;

/// <summary>
/// Параметры возврата робота на зарядку и в парковку из секции <c>Routing:Charging</c>.
/// Эндпоинты <c>move-to-charge</c>/<c>move-to-parking</c> работают всегда,
/// фоновые триггеры включает <see cref="Enabled"/>.
/// </summary>
public sealed class ChargingOptions
{
    /// <summary>Имя секции конфигурации.</summary>
    public const string SectionName = "Routing:Charging";

    /// <summary>Включает фоновые триггеры низкого заряда и простоя.</summary>
    public bool Enabled { get; set; }

    /// <summary>Карта автопереездов, если в <c>state</c> робота <c>mapId</c> пуст.</summary>
    public string MapId { get; set; } = "801";

    /// <summary>Порог низкого заряда, проценты: ниже него робот едет на <c>CHARGING</c>.</summary>
    public double LowerLimit { get; set; } = 20;

    /// <summary>Порог «заряд завершен», проценты.</summary>
    public double TargetLimit { get; set; } = 80;

    /// <summary>Простой до парковки, минуты.</summary>
    public double WaitingMinutes { get; set; } = 5;

    /// <summary>Интервал опроса фонового сервиса, секунды.</summary>
    public double PollSeconds { get; set; } = 5;

    /// <summary>Пауза между попытками автопереезда, секунды: занятый узел либо отказ заказа.</summary>
    public double RetryDelaySeconds { get; set; } = 60;

    /// <summary>Предел подряд отложенных попыток на робота, после него попытки прекращаются.</summary>
    public int MaxAttempts { get; set; } = 60;

    /// <summary>Радиус, в котором робот считается стоящим на узле, метры.</summary>
    public double IdleNodeToleranceMeters { get; set; } = 0.5;

    /// <summary>Радиус, в котором узел считается занятым другим роботом, метры.</summary>
    public double OccupiedRadiusMeters { get; set; } = 0.75;

    /// <summary>Идентификатор зарядного устройства: параметр <c>chg_id</c> действия <c>startCharging</c>.</summary>
    public string ChargerId { get; set; } = "20373";

    /// <summary>Публиковать событие завершения заряда.</summary>
    public bool PublishChargingCompleted { get; set; } = true;

    /// <summary>Топик события завершения заряда.</summary>
    public string ChargingCompletedTopic { get; set; } = "agv-charging-completed";

    /// <summary>Отправлять <c>stopCharging</c> после достижения <see cref="TargetLimit"/>.</summary>
    public bool AutoStopCharging { get; set; }

    /// <summary>
    /// Проверяет параметры на старте сервиса.
    /// </summary>
    /// <returns>Текст ошибки с полным именем ключа либо <c>null</c>, если параметры корректны.</returns>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(MapId))
        {
            return "Routing:Charging:MapId не задан: непонятно, на какой карте искать узлы зарядки и парковки.";
        }

        if (LowerLimit <= 0)
        {
            return $"Routing:Charging:LowerLimit должен быть больше нуля, получено {LowerLimit}.";
        }

        if (TargetLimit > 100)
        {
            return $"Routing:Charging:TargetLimit не может быть больше 100, получено {TargetLimit}.";
        }

        if (LowerLimit >= TargetLimit)
        {
            return "Routing:Charging:LowerLimit должен быть меньше Routing:Charging:TargetLimit, "
                + $"получено LowerLimit={LowerLimit}, TargetLimit={TargetLimit}.";
        }

        if (WaitingMinutes <= 0)
        {
            return $"Routing:Charging:WaitingMinutes должен быть больше нуля, получено {WaitingMinutes}.";
        }

        if (PollSeconds <= 0)
        {
            return $"Routing:Charging:PollSeconds должен быть больше нуля, получено {PollSeconds}.";
        }

        if (RetryDelaySeconds < 0)
        {
            return $"Routing:Charging:RetryDelaySeconds не может быть отрицательным, получено {RetryDelaySeconds}.";
        }

        if (MaxAttempts <= 0)
        {
            return $"Routing:Charging:MaxAttempts должен быть больше нуля, получено {MaxAttempts}.";
        }

        if (IdleNodeToleranceMeters <= 0)
        {
            return $"Routing:Charging:IdleNodeToleranceMeters должен быть больше нуля, получено {IdleNodeToleranceMeters}.";
        }

        if (OccupiedRadiusMeters <= 0)
        {
            return $"Routing:Charging:OccupiedRadiusMeters должен быть больше нуля, получено {OccupiedRadiusMeters}.";
        }

        if (PublishChargingCompleted && string.IsNullOrWhiteSpace(ChargingCompletedTopic))
        {
            return "Routing:Charging:ChargingCompletedTopic не задан, хотя Routing:Charging:PublishChargingCompleted включен.";
        }

        return null;
    }
}

/// <summary>
/// Проверка <see cref="ChargingOptions"/> при старте сервиса.
/// </summary>
public sealed class ChargingOptionsValidator : IValidateOptions<ChargingOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ChargingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var error = options.Validate();
        return error is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error);
    }
}
