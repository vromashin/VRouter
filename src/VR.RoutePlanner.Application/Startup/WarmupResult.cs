using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Application.Startup;

/// <summary>
/// Итог одного прогрева ресурсов карты. Числа совпадают с содержимым таблиц <c>vrt_*</c>
/// после записи и отдаются в <c>GET /api/graph/status</c>.
/// </summary>
/// <param name="MapId">Внешний идентификатор карты.</param>
/// <param name="MapVersionId">Идентификатор строки <c>vrt_MapVersion</c>.</param>
/// <param name="GraphVersion">Версия графа, для которой считались ресурсы.</param>
/// <param name="EdgeCount">Число активных ребер карты.</param>
/// <param name="WidthSource">Источник ширин коридоров.</param>
/// <param name="ResourceCount">Число записанных ресурсов по профилям роботов.</param>
/// <param name="ImpassableCount">Число непроходимых по ширине ребер по профилям роботов.</param>
/// <param name="DurationMs">Длительность прогрева в миллисекундах.</param>
/// <param name="ReusedWidths">Ширины взяты из <c>vrt_EdgeWidth</c> без повторного расчета по растру.</param>
/// <param name="CompletedAt">Момент завершения прогрева в UTC.</param>
public sealed record WarmupResult(
    string MapId,
    Guid MapVersionId,
    string GraphVersion,
    int EdgeCount,
    WidthSource WidthSource,
    IReadOnlyDictionary<string, int> ResourceCount,
    IReadOnlyDictionary<string, int> ImpassableCount,
    long DurationMs,
    bool ReusedWidths,
    DateTimeOffset CompletedAt);
