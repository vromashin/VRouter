using Xunit;

namespace VR.RoutePlanner.Tests.Integration.Support;

/// <summary>
/// Тесты, которые пишут в таблицы <c>vrt_*</c> одной и той же версии карты, выполняются
/// последовательно: иначе они удаляют и вставляют строки друг друга и падают на 23505.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VrtWriteCollection
{
    /// <summary>Имя коллекции для <c>[Collection(...)]</c>.</summary>
    public const string Name = "vrt-write";
}
