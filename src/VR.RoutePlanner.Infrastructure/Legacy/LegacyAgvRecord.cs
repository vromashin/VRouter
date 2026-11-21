using System.ComponentModel.DataAnnotations.Schema;

namespace VR.RoutePlanner.Infrastructure.Legacy;

/// <summary>Строка таблицы <c>public."Agvs"</c>, только чтение.</summary>
[Table("Agvs")]
public sealed class LegacyAgvRecord
{
    public Guid Id { get; set; }
    public string? Manufacturer { get; set; }
    public string? SerialNumber { get; set; }
    public string? AgvType { get; set; }
    public string? Kinematic { get; set; }
    public string? CurrentState { get; set; }
    public string? OperatingMode { get; set; }
    public double BatteryCharge { get; set; }
    public double? BatteryVoltage { get; set; }
    public bool IsCharging { get; set; }
    public bool IsPaused { get; set; }
    public bool Driving { get; set; }
    public double? PositionX { get; set; }
    public double? PositionY { get; set; }
    public double? Theta { get; set; }
    public string? MapId { get; set; }
    public string? LastNodeId { get; set; }
    public string? CurrentOrderId { get; set; }
    public double Velocity { get; set; }
    public double MaxSpeed { get; set; }
    public double MaxLoadMass { get; set; }
    public bool HasLoad { get; set; }
    public string? LoadType { get; set; }
    public string? EStopStatus { get; set; }
    public bool FieldViolation { get; set; }
    public DateTime LastUpdateTime { get; set; }
    public string? ConnectionState { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime UpdatedDate { get; set; }
    public double? Weight { get; set; }
}
