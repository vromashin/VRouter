namespace VR.RoutePlanner.Domain.Planning;

/// <summary>
/// Коды причин в <see cref="RoutePlanResult"/> и <see cref="PlannedRoute"/>.
/// Значения стабильны: по ним разбираются логи и ответы API.
/// </summary>
public static class RoutePlanReason
{
    /// <summary>Карта не загружена или отсутствует.</summary>
    public const string MapNotFound = "MAP_NOT_FOUND";

    /// <summary>Узел старта или цели не найден на карте.</summary>
    public const string NodeNotFound = "NODE_NOT_FOUND";

    /// <summary>Узел старта или цели неактивен.</summary>
    public const string NodeInactive = "NODE_INACTIVE";

    /// <summary>Профиль робота отсутствует в <c>Routing:AgvProfiles</c>.</summary>
    public const string ProfileUnknown = "PROFILE_UNKNOWN";

    /// <summary>Старт совпадает с целью.</summary>
    public const string StartEqualsGoal = "START_EQUALS_GOAL";

    /// <summary>Нет строки <c>vrt_MapVersion</c> для текущей версии графа.</summary>
    public const string NoMapVersion = "NO_MAP_VERSION";

    /// <summary>Нет строк <c>vrt_EdgeWidth</c> для версии карты.</summary>
    public const string NoWidths = "NO_WIDTHS";

    /// <summary>Нет строк <c>vrt_EdgePassability</c> для профиля и версии карты.</summary>
    public const string NoPassability = "NO_PASSABILITY";

    /// <summary>Маршрута нет: все направления перекрыты.</summary>
    public const string NoPath = "NO_PATH";

    /// <summary>Безопасный объезд существует, но дороже лимита <c>SafetyOverrideRatio</c>.</summary>
    public const string SafeDetourTooExpensive = "SAFE_DETOUR_TOO_EXPENSIVE";

    /// <summary>Безопасного объезда без узких мест нет.</summary>
    public const string NoSafeDetour = "NO_SAFE_DETOUR";

    /// <summary>У ребра маршрута нет ресурса профиля в наборе ресурсов версии карты.</summary>
    public const string ResourceMissing = "RESOURCE_MISSING";

    /// <summary>Нет точки подхода: целевой узел без проезжих ребер, рядом нет узла с проезжим ребром.</summary>
    public const string NoApproach = "NO_APPROACH";

    /// <summary>Маршрут построен до узла подхода, а не до целевого узла.</summary>
    public const string ApproachNodeUsed = "APPROACH_NODE_USED";
}
