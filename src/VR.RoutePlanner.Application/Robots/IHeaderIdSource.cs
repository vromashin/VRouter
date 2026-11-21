namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Источник <c>headerId</c> сообщений VDA: по таймеру, строго возрастает.
/// </summary>
public interface IHeaderIdSource
{
    /// <summary>
    /// Отдает следующий идентификатор сообщения.
    /// </summary>
    /// <returns>Идентификатор.</returns>
    long Next();
}
