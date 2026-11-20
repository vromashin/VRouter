using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Domain.Graph;

/// <summary>
/// Проходимость ребра для профиля робота: единственный источник истины о том,
/// входит ли ребро в ресурсы и почему исключено.
/// </summary>
/// <param name="MapVersionId">Идентификатор версии карты (<c>vrt_MapVersion."Id"</c>).</param>
/// <param name="EdgeId">Значение <c>public."Edges"."Id"</c>.</param>
/// <param name="AgvProfile">Имя профиля робота.</param>
/// <param name="IsPassable">Пропускает ли коридор профиль по ширине (<c>W &gt;= B + 2*S</c>).</param>
/// <param name="Class">Класс коридора по числу роботов <c>Nmax</c>.</param>
/// <param name="Reason">Причина непроходимости либо <c>null</c> для проходимых ребер.</param>
public sealed record EdgePassability(
    Guid MapVersionId,
    Guid EdgeId,
    string AgvProfile,
    bool IsPassable,
    CorridorClass Class,
    string? Reason);
