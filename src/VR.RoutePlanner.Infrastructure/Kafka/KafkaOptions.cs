namespace VR.RoutePlanner.Infrastructure.Kafka;

/// <summary>Настройки Kafka для чтения задач WMS и публикации событий.</summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Routing:Kafka";

    /// <summary>Включает чтение задач и публикацию событий.</summary>
    public bool Enabled { get; set; }

    /// <summary>Адрес брокера, например <c>127.0.0.1:9092</c>.</summary>
    public string BootstrapServers { get; set; } = "127.0.0.1:9092";

    /// <summary>Группа консьюмера задач.</summary>
    public string GroupId { get; set; } = "vrp-fleet";

    /// <summary>Топик задач от WMS.</summary>
    public string TasksTopic { get; set; } = "simple-tasks";

    /// <summary>С чего читать при первом запуске группы: <c>Latest</c> или <c>Earliest</c>.</summary>
    public string AutoOffsetReset { get; set; } = "Latest";

    /// <summary>Топик событий выполнения задач.</summary>
    public string EventsTopic { get; set; } = "tasks-events";
}
