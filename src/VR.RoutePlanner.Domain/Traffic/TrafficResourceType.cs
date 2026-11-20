namespace VR.RoutePlanner.Domain.Traffic;

/// <summary>
/// Тип транспортного ресурса. Определяет, сколько роботов может находиться в коридоре
/// и как они согласуют движение.
/// </summary>
public enum TrafficResourceType
{
    /// <summary>Полное исключение: в коридоре одновременно не больше одного робота.</summary>
    EXCLUSIVE,

    /// <summary>Движение в одном направлении, встречное запрещено.</summary>
    DIRECTIONAL,

    /// <summary>Параллельное движение нескольких роботов в одном направлении.</summary>
    PARALLEL,

    /// <summary>Зона конфликта вокруг перекрестка или узла повышенной степени.</summary>
    CONFLICT_ZONE,

    /// <summary>Независимый коридор, резервирование не требуется.</summary>
    INDEPENDENT,
}
