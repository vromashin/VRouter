namespace VR.RoutePlanner.Domain.Planning;

/// <summary>
/// Направление прохождения ребра относительно его геометрии.
/// </summary>
public enum RouteEdgeDirection
{
    /// <summary>Движение от <c>FromNodeId</c> к <c>ToNodeId</c>.</summary>
    Forward,

    /// <summary>Движение против геометрии ребра, от <c>ToNodeId</c> к <c>FromNodeId</c>.</summary>
    Backward,
}
