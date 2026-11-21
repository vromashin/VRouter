namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Отличает блокирующие ошибки робота от предупреждений. VDA отдает список
/// <c>errors</c> с уровнем, и предупреждения не должны закрывать заказ.
/// </summary>
public static class RobotErrorClassifier
{
    /// <summary>Признак блокирующей ошибки: есть хотя бы одна не уровня WARNING.</summary>
    public static bool HasBlockingError(RobotSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        foreach (var error in snapshot.Errors)
        {
            if (!IsWarning(error))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWarning(string error) =>
        error.StartsWith("W|", StringComparison.OrdinalIgnoreCase)
        || error.StartsWith("WARNING|", StringComparison.OrdinalIgnoreCase)
        || error.StartsWith("N|", StringComparison.OrdinalIgnoreCase);
}
