namespace VR.RoutePlanner.Application.Legacy;

/// <summary>Настройки записи в legacy-таблицы AGVControl.</summary>
public sealed class LegacyOptions
{
    public const string SectionName = "Legacy";

    /// <summary>
    /// Разрешает обновлять позицию и статус робота в <c>public."Agvs"</c>, чтобы админка
    /// и карта видели движение. Включать только когда старый мастер остановлен.
    /// </summary>
    public bool WriteAgvsEnabled { get; set; }
}
