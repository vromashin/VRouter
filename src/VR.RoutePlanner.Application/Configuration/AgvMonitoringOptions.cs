namespace VR.RoutePlanner.Application.Configuration;

/// <summary>
/// Параметры мониторинга роботов из секции <c>AgvMonitoring</c>.
/// </summary>
public sealed class AgvMonitoringOptions
{
    /// <summary>Имя секции конфигурации.</summary>
    public const string SectionName = "AgvMonitoring";

    /// <summary>Сколько секунд состояние робота считается свежим.</summary>
    public double StaleStateSeconds { get; set; } = 5;

    /// <summary>Проверяет параметры на старте сервиса.</summary>
    /// <returns>Текст ошибки либо <c>null</c>.</returns>
    public string? Validate() =>
        StaleStateSeconds <= 0
            ? $"AgvMonitoring:StaleStateSeconds должен быть больше нуля, получено {StaleStateSeconds}."
            : null;
}
