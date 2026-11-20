namespace VR.RoutePlanner.Domain.Traffic;

/// <summary>
/// Конфликт двух транспортных ресурсов.
/// </summary>
/// <param name="ResourceId">Идентификатор ресурса с меньшим порядком сортировки.</param>
/// <param name="ConflictingResourceId">Идентификатор второго ресурса.</param>
/// <param name="ConflictType">Тип конфликта: <c>SHARED_NODE</c> или <c>GEOMETRIC_OVERLAP</c>.</param>
public sealed record ResourceConflict(
    Guid ResourceId,
    Guid ConflictingResourceId,
    string ConflictType);
