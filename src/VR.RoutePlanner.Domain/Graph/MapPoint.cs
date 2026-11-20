namespace VR.RoutePlanner.Domain.Graph;

/// <summary>
/// Точка в системе координат карты, метры.
/// </summary>
/// <param name="X">Координата X, метры.</param>
/// <param name="Y">Координата Y, метры.</param>
/// <param name="Z">Высота, метры; <c>null</c> если не задана.</param>
public sealed record MapPoint(double X, double Y, double? Z);
