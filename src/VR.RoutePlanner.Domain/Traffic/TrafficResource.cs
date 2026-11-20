namespace VR.RoutePlanner.Domain.Traffic;

/// <summary>
/// Транспортный ресурс: участок графа, на который этап 3 будет ставить резервирование.
/// </summary>
/// <param name="Id">Идентификатор ресурса, детерминированно выводится из <paramref name="Code"/>.</param>
/// <param name="Code">Уникальный код, например <c>801-C2-3</c> или <c>801-NODE-wa152</c>.</param>
/// <param name="ResourceType">Тип ресурса.</param>
/// <param name="Capacity">Сколько роботов допускает ресурс одновременно.</param>
/// <param name="MinEntryInterval">Минимальный интервал между входами, секунды. На этапе 1 равен 0.</param>
/// <param name="MaxOccupancyTime">Максимальное время занятия, секунды. На этапе 1 равен 0.</param>
/// <param name="IsActive">Признак активности ресурса.</param>
public sealed record TrafficResource(
    Guid Id,
    string Code,
    TrafficResourceType ResourceType,
    int Capacity,
    double MinEntryInterval,
    double MaxOccupancyTime,
    bool IsActive);
