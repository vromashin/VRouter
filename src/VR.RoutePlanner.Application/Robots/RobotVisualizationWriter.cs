using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Legacy;

namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Запись живой позиции в <c>public."Agvs"</c> с троттлингом: не чаще двух раз в секунду
/// на робота. Работает только при <c>Legacy:WriteAgvsEnabled</c>.
/// </summary>
public sealed class RobotVisualizationWriter
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(500);

    private readonly ILegacyAgvVisualizationWriter _writer;
    private readonly IOptions<LegacyOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RobotVisualizationWriter> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastWrite = new(StringComparer.Ordinal);

    /// <summary>
    /// Создает писатель позиции.
    /// </summary>
    /// <param name="writer">Запись в legacy-таблицу.</param>
    /// <param name="options">Параметры <c>Legacy</c>.</param>
    /// <param name="time">Источник времени.</param>
    /// <param name="logger">Журнал.</param>
    public RobotVisualizationWriter(
        ILegacyAgvVisualizationWriter writer,
        IOptions<LegacyOptions> options,
        TimeProvider time,
        ILogger<RobotVisualizationWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _writer = writer;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Пишет позицию робота, если прошло не меньше половины секунды с прошлой записи.
    /// </summary>
    /// <param name="visualization">Визуализация.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns><c>true</c>, если строка обновлена.</returns>
    public async Task<bool> WriteAsync(RobotVisualization visualization, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(visualization);

        if (!_options.Value.WriteAgvsEnabled)
        {
            return false;
        }

        var now = _time.GetUtcNow();
        if (_lastWrite.TryGetValue(visualization.SerialNumber, out var last) && now - last < Throttle)
        {
            return false;
        }

        await _writer.ApplyVisualizationAsync(visualization, ct).ConfigureAwait(false);
        _lastWrite[visualization.SerialNumber] = now;
        _logger.LogTrace("Позиция робота {Serial} записана в Agvs", visualization.SerialNumber);
        return true;
    }
}
