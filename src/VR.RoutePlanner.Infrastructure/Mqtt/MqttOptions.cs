namespace VR.RoutePlanner.Infrastructure.Mqtt;

/// <summary>
/// Параметры MQTT-клиента из секции <c>Routing:Mqtt</c>. Секретов в конфиге нет:
/// имя пользователя и пароль приходят из окружения.
/// </summary>
public sealed class MqttOptions
{
    /// <summary>Имя вложенной секции конфигурации.</summary>
    public const string SectionName = "Mqtt";

    /// <summary>Хост брокера.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Порт брокера.</summary>
    public int Port { get; set; } = 1883;

    /// <summary>Имя пользователя; пустое значение — анонимное подключение.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Пароль; в конфиг не пишется, задается окружением.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Префикс идентификатора клиента: к нему дописывается имя процесса.</summary>
    public string ClientIdPrefix { get; set; } = "vr-route-planner";

    /// <summary>Интервал keep-alive, секунды.</summary>
    public int KeepAliveSeconds { get; set; } = 30;

    /// <summary>Предельная пауза между попытками переподключения, секунды.</summary>
    public int ReconnectMaxSeconds { get; set; } = 30;

    /// <summary>Признак включения чтения состояния роботов по MQTT.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Проверяет параметры на старте сервиса.
    /// </summary>
    /// <returns>Текст ошибки либо <c>null</c>, если параметры корректны.</returns>
    public string? Validate()
    {
        if (!Enabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            return "Routing:Mqtt:Host не задан.";
        }

        if (Port is <= 0 or > 65535)
        {
            return $"Routing:Mqtt:Port должен быть в диапазоне 1..65535, получено {Port}.";
        }

        if (string.IsNullOrWhiteSpace(ClientIdPrefix))
        {
            return "Routing:Mqtt:ClientIdPrefix не задан.";
        }

        if (KeepAliveSeconds is <= 0 or > 3600)
        {
            return $"Routing:Mqtt:KeepAliveSeconds должен быть в диапазоне 1..3600, получено {KeepAliveSeconds}.";
        }

        if (ReconnectMaxSeconds <= 0)
        {
            return $"Routing:Mqtt:ReconnectMaxSeconds должен быть больше нуля, получено {ReconnectMaxSeconds}.";
        }

        return null;
    }
}
