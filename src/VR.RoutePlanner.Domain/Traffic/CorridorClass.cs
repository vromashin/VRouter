namespace VR.RoutePlanner.Domain.Traffic;

/// <summary>
/// Класс коридора по пропускной способности, от C1 (один робот) до C5 (пять и более).
/// </summary>
public enum CorridorClass
{
    /// <summary>Один робот в коридоре.</summary>
    C1,

    /// <summary>Два робота в коридоре.</summary>
    C2,

    /// <summary>Три робота в коридоре.</summary>
    C3,

    /// <summary>Четыре робота в коридоре.</summary>
    C4,

    /// <summary>Пять и более роботов в коридоре.</summary>
    C5,
}
