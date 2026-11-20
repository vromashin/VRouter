using VR.RoutePlanner.Domain.Graph;

namespace VR.RoutePlanner.Domain.Traffic;

/// <summary>
/// Полный набор ресурсов одного профиля робота: ресурсы, их связи с ребрами и узлами, конфликты.
/// Наборы разных профилей независимы: одно ребро или узел может принадлежать ресурсам разных профилей.
/// </summary>
/// <param name="Profile">Профиль робота, для которого построен набор.</param>
/// <param name="Resources">Ресурсы ребер и узлов.</param>
/// <param name="ResourceEdges">Связи с ребрами.</param>
/// <param name="ResourceNodes">Связи с узлами.</param>
/// <param name="Conflicts">Конфликты между коридорными ресурсами этого профиля.</param>
public sealed record ProfileResourceSet(
    AgvProfile Profile,
    IReadOnlyList<TrafficResource> Resources,
    IReadOnlyList<TrafficResourceEdge> ResourceEdges,
    IReadOnlyList<TrafficResourceNode> ResourceNodes,
    IReadOnlyList<ResourceConflict> Conflicts,
    IReadOnlyList<EdgePassability> Passability)
{
    /// <summary>Имя профиля, дублирует <c>Profile.Name</c> для удобства запросов.</summary>
    public string AgvProfile => Profile.Name;
}
