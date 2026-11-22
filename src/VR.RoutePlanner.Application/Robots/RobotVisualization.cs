namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Позиция и телеметрия робота из топика <c>visualization</c> VDA 5050 (5 Гц).
/// </summary>
/// <param name="SerialNumber">Серийный номер робота.</param>
/// <param name="Manufacturer">Производитель.</param>
/// <param name="Version">Версия протокола.</param>
/// <param name="PositionX">Координата X, метры.</param>
/// <param name="PositionY">Координата Y, метры.</param>
/// <param name="Theta">Ориентация, радианы.</param>
/// <param name="MapId">Идентификатор карты.</param>
/// <param name="BatteryCharge">Заряд батареи, проценты.</param>
/// <param name="Driving">Робот в движении.</param>
/// <param name="Paused">Робот на паузе.</param>
/// <param name="EStop">Состояние аварийного останова.</param>
/// <param name="FieldViolation">Нарушение защитного поля.</param>
/// <param name="LiftHeight">Высота подъема вил, метры.</param>
/// <param name="Velocity">Модуль скорости, метры в секунду.</param>
/// <param name="ReceivedAt">Момент получения сообщения.</param>
public sealed record RobotVisualization(
    string SerialNumber,
    string? Manufacturer,
    string Version,
    double PositionX,
    double PositionY,
    double Theta,
    string? MapId,
    double BatteryCharge,
    bool Driving,
    bool Paused,
    string? EStop,
    bool FieldViolation,
    double? LiftHeight,
    double Velocity,
    DateTimeOffset ReceivedAt);

/// <summary>
/// Чтение последней визуализации роботов из памяти процесса, читающего MQTT.
/// </summary>
public interface IRobotVisualizationReader
{
    /// <summary>
    /// Отдает последнюю визуализацию робота.
    /// </summary>
    /// <param name="serialNumber">Серийный номер робота.</param>
    /// <returns>Визуализация либо <c>null</c>.</returns>
    RobotVisualization? GetLatest(string serialNumber);
}
