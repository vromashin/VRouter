namespace VR.RoutePlanner.Domain.Traffic;

/// <summary>
/// Транспортный ресурс: участок графа, на который этап 3 будет ставить резервирование.
/// </summary>
/// <param name="Id">Идентификатор ресурса, детерминированно выводится из кода и профиля.</param>
/// <param name="MapVersionId">Значение <c>vrt_MapVersion.Id</c>.</param>
/// <param name="Code">
/// Код вида <c>{MapId}-{AgvProfile}-{CorridorClass}-{n}</c> для коридорных ресурсов,
/// <c>{MapId}-NODE-{NodeId}</c> и <c>{MapId}-CZ-{NodeId}</c> для узловых.
/// </param>
/// <param name="AgvProfile">Имя профиля робота, для которого построен ресурс.</param>
/// <param name="ResourceType">Тип ресурса.</param>
/// <param name="Capacity">Сколько роботов профиля допускает ресурс одновременно.</param>
/// <param name="MinEntryInterval">Минимальный интервал между входами, секунды. На этапе 1 равен 0.</param>
/// <param name="MaxOccupancyTime">Максимальное время занятия, секунды. На этапе 1 равен 0.</param>
/// <param name="IsActive">Признак активности ресурса.</param>
public sealed record TrafficResource(
    Guid Id,
    Guid MapVersionId,
    string Code,
    string AgvProfile,
    TrafficResourceType ResourceType,
    int Capacity,
    double MinEntryInterval,
    double MaxOccupancyTime,
    bool IsActive);
