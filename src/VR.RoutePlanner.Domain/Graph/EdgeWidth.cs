using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Domain.Graph;

/// <summary>
/// Ширина и длина ребра: считаются один раз на версию карты и от профиля не зависят.
/// </summary>
/// <param name="MapVersionId">Идентификатор <c>vrt_MapVersion.Id</c>.</param>
/// <param name="EdgeId">Идентификатор <c>public."Edges"."Id"</c>.</param>
/// <param name="WidthMeters">Ширина коридора W в метрах.</param>
/// <param name="LengthMeters">Длина траектории в метрах.</param>
/// <param name="SampledPoints">Число точек траектории.</param>
/// <param name="WidthSource">Источник значения ширины.</param>
/// <param name="WidthSourceDetail">Уточнение источника: файл растра или ключ коридора.</param>
public sealed record EdgeWidth(
    Guid MapVersionId,
    Guid EdgeId,
    double WidthMeters,
    double LengthMeters,
    int SampledPoints,
    WidthSource WidthSource,
    string? WidthSourceDetail);
