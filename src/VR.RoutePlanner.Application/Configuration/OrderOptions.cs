namespace VR.RoutePlanner.Application.Configuration;

/// <summary>
/// Параметры отправки заказов из секции <c>Routing:Order</c>.
/// </summary>
public sealed class OrderOptions
{
    /// <summary>Имя вложенной секции конфигурации.</summary>
    public const string SectionName = "Order";

    /// <summary>Сколько первых узлов заказа отправлять роботу выпущенными.</summary>
    public int ReleasedNodes { get; set; } = 3;

    /// <summary>Предельное время без прогресса узлов, секунды.</summary>
    public double ProgressTimeoutSeconds { get; set; } = 60;

    /// <summary>Ожидание подтверждения заказа роботом, секунды.</summary>
    public double AcceptTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Ожидание, пока робот поднимет <c>orderUpdateId</c> до опубликованного обновления, секунды.
    /// Дольше — заказ проваливается с <c>UPDATE_NOT_ACCEPTED</c>.
    /// </summary>
    public double UpdateAcceptTimeoutSeconds { get; set; } = 10;

    /// <summary>Молчание робота по <c>state</c>, после которого заказ проваливается, секунды.</summary>
    public double StateSilenceSeconds { get; set; } = 15;

    /// <summary>Производитель робота для топиков и заказа.</summary>
    public string Manufacturer { get; set; } = "Tuskrobots";

    /// <summary>
    /// Отправлять действия pick/drop в заказе. На симуляторе TUSKROBOTS действие не завершается,
    /// робот навсегда остается в WORKING, поэтому на стенде флаг выключается.
    /// </summary>
    public bool SendForkActions { get; set; } = true;

    /// <summary>
    /// Действие в конечном узле заказа. Нужно симулятору: без действия список точек остановки пуст,
    /// и MotionController зависает. Сами вилы выполняются отдельным instantAction.
    /// </summary>
    public string? TerminalActionType { get; set; } = "detectObject";

    /// <summary>
    /// Отдавать курс робота в первом узле первого сообщения, когда поза известна.
    /// Эталон AGVControl на стенде отдает курс фактической позы (снимок ref-pick-drop, wo-2-7).
    /// </summary>
    public bool FirstNodeTheta { get; set; } = true;

    /// <summary>Предельное число обновлений плана на один заказ.</summary>
    public int MaxOrderUpdates { get; set; } = 32;

    /// <summary>Проверяет параметры на старте сервиса.</summary>
    /// <returns>Текст ошибки либо <c>null</c>.</returns>
    public string? Validate()
    {
        if (ReleasedNodes < 0)
        {
            return $"Routing:Order:ReleasedNodes не может быть отрицательным, получено {ReleasedNodes}.";
        }

        if (MaxOrderUpdates <= 0)
        {
            return $"Routing:Order:MaxOrderUpdates должен быть больше нуля, получено {MaxOrderUpdates}.";
        }

        if (ProgressTimeoutSeconds <= 0 || AcceptTimeoutSeconds <= 0 || StateSilenceSeconds <= 0)
        {
            return "Таймауты Routing:Order должны быть больше нуля.";
        }

        if (UpdateAcceptTimeoutSeconds <= 0)
        {
            return $"Routing:Order:UpdateAcceptTimeoutSeconds должен быть больше нуля, получено {UpdateAcceptTimeoutSeconds}.";
        }

        if (string.IsNullOrWhiteSpace(Manufacturer))
        {
            return "Routing:Order:Manufacturer не задан.";
        }

        return null;
    }
}
