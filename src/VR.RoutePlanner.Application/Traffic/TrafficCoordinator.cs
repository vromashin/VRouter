using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Application.Traffic;

/// <summary>
/// Диспетчер трафика: считает горизонт робота по сохраненному плану и ведет резервы
/// в <see cref="TrafficReservationRegistry"/>.
///
/// Резерв ставится только по <c>vrt_TrafficResource.Id</c> и только на то, что робот держит
/// впереди себя: проеханное получает <c>PassedAt</c> и снимается через <c>ReleaseHorizonSeconds</c>.
/// Отказ трафика на <c>state</c> не меняет статус заказа: он только логируется.
/// </summary>
public sealed class TrafficCoordinator : ITrafficCoordinator
{
    private readonly ITrafficResourceLookup _lookup;
    private readonly TrafficReservationRegistry _registry;
    private readonly IOptions<RoutePlannerOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<TrafficCoordinator> _logger;

    /// <summary>
    /// Создает диспетчер трафика.
    /// </summary>
    /// <param name="lookup">Разрешение ресурсов карты и профиля.</param>
    /// <param name="registry">Реестр резервов.</param>
    /// <param name="options">Параметры <c>Routing</c>.</param>
    /// <param name="time">Источник времени.</param>
    /// <param name="logger">Журнал.</param>
    public TrafficCoordinator(
        ITrafficResourceLookup lookup,
        TrafficReservationRegistry registry,
        IOptions<RoutePlannerOptions> options,
        TimeProvider time,
        ILogger<TrafficCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _lookup = lookup;
        _registry = registry;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TrafficReservationResult> TryReserveForOrderAsync(
        RobotSnapshot snapshot,
        string? planJson,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var resolved = await ResolveAsync(snapshot, planJson, ct).ConfigureAwait(false);
        if (resolved is not { } value)
        {
            return TrafficReservationResult.NoResource(snapshot.MapId);
        }

        var result = _registry.TryAcquire(
            snapshot.SerialNumber,
            value.Map.MapVersionId,
            value.Horizon.Entries,
            _time.GetUtcNow());

        if (!result.Accepted)
        {
            _logger.LogInformation(
                "Робот {Serial}: заказ не выдан, трафик занят ресурсом {Resource} робота {Owner}",
                snapshot.SerialNumber,
                result.BlockedResourceCode,
                result.BlockedBySerial);
            return result;
        }

        return MissingEdges(snapshot, value.Horizon) ?? result;
    }

    /// <inheritdoc />
    public async Task<TrafficReservationResult> OnStateAsync(
        RobotSnapshot snapshot,
        string? planJson,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var resolved = await ResolveAsync(snapshot, planJson, ct).ConfigureAwait(false);
        if (resolved is not { } value)
        {
            return TrafficReservationResult.NoResource(snapshot.MapId);
        }

        var result = _registry.Renew(
            snapshot.SerialNumber,
            value.Map.MapVersionId,
            value.Horizon.Entries,
            _time.GetUtcNow());

        if (!result.Accepted)
        {
            // Отказ на state заказ не рвет: робот уже едет, резерв доберем следующим сообщением.
            _logger.LogWarning(
                "Робот {Serial}: горизонт добрать не удалось, ресурс {Resource} держит {Owner}",
                snapshot.SerialNumber,
                result.BlockedResourceCode,
                result.BlockedBySerial);
        }

        return MissingEdges(snapshot, value.Horizon) ?? result;
    }

    /// <inheritdoc />
    public Task<int> ReleaseAsync(string agvSerialNumber, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvSerialNumber);

        var released = _registry.Release(agvSerialNumber);
        if (released > 0)
        {
            _logger.LogInformation("Робот {Serial}: снято резервов {Count}", agvSerialNumber, released);
        }

        return Task.FromResult(released);
    }

    /// <inheritdoc />
    public Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        var removed = _registry.Sweep(now);
        foreach (var owner in removed
            .Select(item => item.OwnerSerial)
            .Distinct(StringComparer.Ordinal))
        {
            _logger.LogWarning(
                "Робот {Serial}: резервы снял свип, подтверждения state нет дольше {Seconds} с",
                owner,
                _options.Value.ReservationHorizonSeconds);
        }

        return Task.FromResult(removed.Count);
    }

    /// <summary>
    /// Ребра плана без ресурса: повод вернуть <c>TRAFFIC_NO_RESOURCE</c> и записать предупреждение,
    /// а не молча пускать (риск Р-5).
    /// </summary>
    private TrafficReservationResult? MissingEdges(RobotSnapshot snapshot, TrafficHorizon horizon)
    {
        if (horizon.MissingEdgeIds.Count == 0)
        {
            return null;
        }

        _logger.LogWarning(
            "Робот {Serial}: ресурс не найден для ребер {Edges} (TRAFFIC_NO_RESOURCE)",
            snapshot.SerialNumber,
            string.Join(", ", horizon.MissingEdgeIds));

        return TrafficReservationResult.NoResource(horizon.MissingEdgeIds[0]);
    }

    /// <summary>
    /// Разрешает ресурсы карты и считает горизонт по плану.
    /// </summary>
    /// <returns><c>null</c>, если плана нет, карты нет или ресурсов профиля нет.</returns>
    private async Task<(ITrafficResourceMap Map, TrafficHorizon Horizon)?> ResolveAsync(
        RobotSnapshot snapshot,
        string? planJson,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(planJson) || string.IsNullOrWhiteSpace(snapshot.MapId))
        {
            return null;
        }

        var options = _options.Value;
        var map = await _lookup
            .GetAsync(snapshot.MapId!, options.DefaultAgvProfile, ct)
            .ConfigureAwait(false);

        if (map is null)
        {
            _logger.LogWarning(
                "Робот {Serial}: ресурсов трафика для карты {Map} и профиля {Profile} нет (TRAFFIC_NO_RESOURCE)",
                snapshot.SerialNumber,
                snapshot.MapId,
                options.DefaultAgvProfile);
            return null;
        }

        var horizon = TrafficHorizonCalculator.Calculate(
            planJson,
            snapshot.LastNodeSequenceId,
            map,
            options);

        return (map, horizon);
    }
}
