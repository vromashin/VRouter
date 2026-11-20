using System.Globalization;
using Microsoft.Extensions.Logging;

namespace VR.RoutePlanner.Tests.Unit.Support;

/// <summary>
/// Логгер, сохраняющий сообщения для проверок.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        _entries.Add((logLevel, message));
    }

    public IReadOnlyList<string> Warnings => _entries
        .Where(entry => entry.Level == LogLevel.Warning)
        .Select(entry => entry.Message)
        .ToList();

    public string Describe() => string.Join(
        Environment.NewLine,
        _entries.Select(entry => string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.Level}: {entry.Message}")));
}
