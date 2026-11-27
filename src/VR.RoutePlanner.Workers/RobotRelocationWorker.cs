using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Robots;

namespace VR.RoutePlanner.Workers;

/// <summary>
/// Фоновый сервис автопереездов (Д-6): низкий заряд — на ближайший свободный узел <c>CHARGING</c>,
/// простой — на ближайший свободный узел <c>PARKING</c>. Тик обслуживает роботов пакетом:
/// сначала зарядка (сортировка по заряду), затем парковка (сортировка по длительности простоя),
/// затем снятие резервов. В памяти живут только таймеры простоя, защёлки событий, счётчики
/// попыток и резервы узлов: состояние робота выводится заново из базы и <c>state</c>.
/// </summary>
public sealed class RobotRelocationWorker : BackgroundService
{
    private readonly IRobotStateReader _states;
    private readonly IRobotOrderRepository _orders;
    private readonly RobotRelocationService _relocation;
    private readonly IOptions<ChargingOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RobotRelocationWorker> _logger;

    /// <summary>Начало простоя робота: по нему считается ожидание до парковки.</summary>
    private readonly Dictionary<string, DateTimeOffset> _idleSince = new(StringComparer.Ordinal);

    /// <summary>Момент последней попытки автопереезда, успешной или нет.</summary>
    private readonly Dictionary<string, DateTimeOffset> _lastAttempt = new(StringComparer.Ordinal);

    /// <summary>Подряд отложенные попытки на робота.</summary>
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);

    /// <summary>Триггер, к которому относятся текущие попытки: зарядка или парковка.</summary>
    private readonly Dictionary<string, string> _attemptTrigger = new(StringComparer.Ordinal);

    /// <summary>Роботы, у которых попытки прекращены по <c>MaxAttempts</c>: одно предупреждение.</summary>
    private readonly HashSet<string> _attemptsStopped = new(StringComparer.Ordinal);

    /// <summary>Защёлка события завершения заряда на робота.</summary>
    private readonly HashSet<string> _completedSent = new(StringComparer.Ordinal);

    /// <summary>
    /// Создает сервис автопереездов.
    /// </summary>
    /// <param name="states">Последние состояния роботов.</param>
    /// <param name="orders">Хранилище заказов: авторитетная занятость узлов.</param>
    /// <param name="relocation">Сервис автопереездов.</param>
    /// <param name="options">Параметры <c>Routing:Charging</c>.</param>
    /// <param name="time">Источник времени.</param>
    /// <param name="logger">Журнал.</param>
    public RobotRelocationWorker(
        IRobotStateReader states,
        IRobotOrderRepository orders,
        RobotRelocationService relocation,
        IOptions<ChargingOptions> options,
        TimeProvider time,
        ILogger<RobotRelocationWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(relocation);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _states = states;
        _orders = orders;
        _relocation = relocation;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation("Автопереезды на зарядку и парковку выключены");
            return;
        }

        var poll = TimeSpan.FromSeconds(Math.Max(options.PollSeconds, 0.1));
        _logger.LogInformation(
            "Автопереезды включены: опрос {Poll:F0} с, зарядка ниже {Lower:F0}%, парковка после {Waiting:F0} мин простоя",
            poll.TotalSeconds,
            options.LowerLimit,
            options.WaitingMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Одна ошибка не останавливает сервис и не блокирует остальных роботов.
                _logger.LogError(exception, "Тик автопереездов завершился ошибкой");
            }

            try
            {
                await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Один тик: состояния роботов, события заряда, пакеты зарядки и парковки, резервы узлов.
    /// Исключение по одному роботу ловится локально и логируется с серийником.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    public async Task TickAsync(CancellationToken ct)
    {
        var options = _options.Value;
        var now = _time.GetUtcNow();

        var states = await _states.GetAllAsync(ct).ConfigureAwait(false);
        var activeOrders = await _orders.GetActiveAsync(ct).ConfigureAwait(false);
        var activeBySerial = activeOrders
            .GroupBy(order => order.AgvSerialNumber, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        // Карты тика: один запрос на карту, дальше кэш репозитория графов.
        var graphs = await LoadGraphsAsync(states, ct).ConfigureAwait(false);

        // 1. Состояние автомата и таймер простоя по каждому роботу.
        var machines = new Dictionary<string, RobotRelocationState>(StringComparer.Ordinal);
        foreach (var snapshot in states.OrderBy(item => item.SerialNumber, StringComparer.Ordinal))
        {
            try
            {
                var graph = graphs.GetValueOrDefault(_relocation.ResolveMapId(snapshot));
                activeBySerial.TryGetValue(snapshot.SerialNumber, out var activeOrder);
                var state = RobotRelocationStateMachine.Determine(
                    snapshot,
                    activeOrder,
                    graph,
                    options.IdleNodeToleranceMeters);
                // Простой считается и для заряженного робота на станции: иначе он никогда
                // не освободит зарядку, а второй робот будет ждать вхолостую.
                var countsAsIdle = state == RobotRelocationState.Idle
                    || (state == RobotRelocationState.Charging && snapshot.BatteryCharge >= options.TargetLimit);
                TrackIdle(snapshot.SerialNumber, countsAsIdle, now);
                machines[snapshot.SerialNumber] = state;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(
                    exception,
                    "Робот {Serial}: состояние автомата не определено",
                    snapshot.SerialNumber);
            }
        }

        // 2. Событие завершения заряда по каждому роботу в состоянии Charging.
        await PublishChargingCompletedAsync(states, machines, graphs, options, ct).ConfigureAwait(false);

        var occupancy = _relocation.BuildOccupancy(states, activeOrders);

        // 3. Пакет зарядки: заявки → сортировка по заряду и серийнику → выдача по одной.
        var charged = await ChargeBatchAsync(states, machines, occupancy, now, options, ct).ConfigureAwait(false);

        // 4. Пакет парковки: роботы в Idle, у кого истекло WaitingMinutes и кто не получил
        //    заказ на зарядку в этом тике.
        await ParkingBatchAsync(states, machines, occupancy, charged, now, options, ct).ConfigureAwait(false);

        // 5. Резервы терминальных заказов и ушедших роботов.
        var released = _relocation.ReleaseReservations(activeOrders, states, now);
        if (released > 0)
        {
            _logger.LogDebug("Снято резервов узлов: {Count}", released);
        }
    }

    private async Task<HashSet<string>> ChargeBatchAsync(
        IReadOnlyCollection<RobotSnapshot> states,
        Dictionary<string, RobotRelocationState> machines,
        RelocationOccupancy occupancy,
        DateTimeOffset now,
        ChargingOptions options,
        CancellationToken ct)
    {
        var charged = new HashSet<string>(StringComparer.Ordinal);

        // Д-3.5: первым едет робот с самым низким зарядом, тай-брейк по серийному номеру.
        // Стоящий в парковке робот тоже кандидат: состояние Parked не мешает зарядке,
        // иначе разряженный робот остался бы в парковке навсегда (проверено на стенде).
        var candidates = states
            .Where(snapshot => IsFreeForCharge(machines, snapshot.SerialNumber))
            .Where(snapshot => snapshot.BatteryCharge < options.LowerLimit)
            .Where(snapshot => RetryAllowed(snapshot.SerialNumber, RelocationNodeTypes.Charging, now, options))
            .OrderBy(snapshot => snapshot.BatteryCharge)
            .ThenBy(snapshot => snapshot.SerialNumber, StringComparer.Ordinal)
            .ToList();

        foreach (var snapshot in candidates)
        {
            var result = await TryRelocateAsync(
                snapshot,
                RelocationNodeTypes.Charging,
                occupancy,
                now,
                options,
                ct).ConfigureAwait(false);
            if (result.Success)
            {
                charged.Add(snapshot.SerialNumber);
            }
        }

        return charged;
    }

    private async Task ParkingBatchAsync(
        IReadOnlyCollection<RobotSnapshot> states,
        Dictionary<string, RobotRelocationState> machines,
        RelocationOccupancy occupancy,
        HashSet<string> charged,
        DateTimeOffset now,
        ChargingOptions options,
        CancellationToken ct)
    {
        var waiting = TimeSpan.FromMinutes(options.WaitingMinutes);

        // Д-3.5: первым паркуется тот, кто дольше простаивает, тай-брейк по серийному номеру.
        // Робот с зарядом ниже LowerLimit в парковку не попадает: зарядка приоритетнее, иначе
        // он попеременно дергал бы оба триггера и сбрасывал счетчик попыток.
        // Заряженный робот на станции освобождает ее: иначе единственная зарядка занята вечно,
        // а второй робот с низким зарядом ждет без пользы (проверено разбором кода).
        var candidates = states
            .Where(snapshot => IsParkingCandidate(machines, snapshot, options))
            .Where(snapshot => snapshot.BatteryCharge >= options.LowerLimit)
            .Where(snapshot => !charged.Contains(snapshot.SerialNumber))
            .Where(snapshot => _idleSince.TryGetValue(snapshot.SerialNumber, out var idleSince)
                && now - idleSince >= waiting)
            .Where(snapshot => RetryAllowed(snapshot.SerialNumber, RelocationNodeTypes.Parking, now, options))
            .OrderBy(snapshot => _idleSince[snapshot.SerialNumber])
            .ThenBy(snapshot => snapshot.SerialNumber, StringComparer.Ordinal)
            .ToList();

        foreach (var snapshot in candidates)
        {
            await TryRelocateAsync(
                snapshot,
                RelocationNodeTypes.Parking,
                occupancy,
                now,
                options,
                ct).ConfigureAwait(false);
        }
    }

    private async Task<RobotRelocationResult> TryRelocateAsync(
        RobotSnapshot snapshot,
        string nodeType,
        RelocationOccupancy occupancy,
        DateTimeOffset now,
        ChargingOptions options,
        CancellationToken ct)
    {
        var serial = snapshot.SerialNumber;
        RobotRelocationResult result;

        try
        {
            result = await _relocation
                .TryRelocateAsync(snapshot, nodeType, null, occupancy, ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Робот {Serial}: автопереезд на {Type} не создан", serial, nodeType);
            result = RobotRelocationResult.Failure("RELOCATION_FAILED");
        }

        // Пауза между попытками считается и после отказа: иначе тик будет дергать заказ каждые
        // PollSeconds и спамить в журнал (Д-5).
        _lastAttempt[serial] = now;

        if (result.Success)
        {
            _attempts[serial] = 0;
            _attemptsStopped.Remove(serial);
            return result;
        }

        _attempts.TryGetValue(serial, out var attempts);
        attempts++;
        _attempts[serial] = attempts;

        if (attempts >= options.MaxAttempts)
        {
            if (_attemptsStopped.Add(serial))
            {
                _logger.LogWarning(
                    "Релокация {Serial} приостановлена: {Attempts} попыток, все узлы {Type} заняты",
                    serial,
                    attempts,
                    nodeType);
            }

            return result;
        }

        _logger.LogInformation(
            "Робот {Serial}: автопереезд на {Type} отложен ({Reason}, попытка {Attempt}/{Max})",
            serial,
            nodeType,
            result.Message,
            attempts,
            options.MaxAttempts);

        return result;
    }

    private async Task PublishChargingCompletedAsync(
        IReadOnlyCollection<RobotSnapshot> states,
        Dictionary<string, RobotRelocationState> machines,
        Dictionary<string, WarehouseGraph?> graphs,
        ChargingOptions options,
        CancellationToken ct)
    {
        foreach (var snapshot in states.OrderBy(item => item.SerialNumber, StringComparer.Ordinal))
        {
            var serial = snapshot.SerialNumber;

            // Защёлка снимается, когда заряд упал ниже LowerLimit: следующий заезд снова даст событие.
            if (snapshot.BatteryCharge < options.LowerLimit)
            {
                _completedSent.Remove(serial);
                continue;
            }

            if (!IsCharging(machines, serial) || _completedSent.Contains(serial))
            {
                continue;
            }

            try
            {
                var graph = graphs.GetValueOrDefault(_relocation.ResolveMapId(snapshot));
                var published = await _relocation
                    .PublishChargingCompletedAsync(snapshot, graph, ct)
                    .ConfigureAwait(false);
                if (published)
                {
                    _completedSent.Add(serial);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(
                    exception,
                    "Робот {Serial}: событие завершения заряда не отправлено",
                    serial);
            }
        }
    }

    private async Task<Dictionary<string, WarehouseGraph?>> LoadGraphsAsync(
        IReadOnlyCollection<RobotSnapshot> states,
        CancellationToken ct)
    {
        var result = new Dictionary<string, WarehouseGraph?>(StringComparer.Ordinal);
        foreach (var snapshot in states)
        {
            var mapId = _relocation.ResolveMapId(snapshot);
            if (result.ContainsKey(mapId))
            {
                continue;
            }

            try
            {
                result[mapId] = await _relocation.LoadGraphAsync(snapshot, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Карта {Map}: граф недоступен", mapId);
                result[mapId] = null;
            }
        }

        return result;
    }

    private bool RetryAllowed(string serial, string nodeType, DateTimeOffset now, ChargingOptions options)
    {
        // Смена триггера сбрасывает счетчик попыток (Д-5).
        if (_attemptTrigger.TryGetValue(serial, out var trigger)
            && !string.Equals(trigger, nodeType, StringComparison.Ordinal))
        {
            ResetAttempts(serial);
        }

        _attemptTrigger[serial] = nodeType;

        if (_attemptsStopped.Contains(serial))
        {
            return false;
        }

        return !_lastAttempt.TryGetValue(serial, out var last)
            || now - last >= TimeSpan.FromSeconds(options.RetryDelaySeconds);
    }

    private void TrackIdle(string serial, bool countsAsIdle, DateTimeOffset now)
    {
        if (countsAsIdle)
        {
            _idleSince.TryAdd(serial, now);
            return;
        }

        _idleSince.Remove(serial);
        ResetAttempts(serial);
    }

    private void ResetAttempts(string serial)
    {
        _lastAttempt.Remove(serial);
        _attempts.Remove(serial);
        _attemptTrigger.Remove(serial);
        _attemptsStopped.Remove(serial);
    }

    private static bool IsIdle(Dictionary<string, RobotRelocationState> machines, string serial) =>
        machines.TryGetValue(serial, out var state) && state == RobotRelocationState.Idle;

    /// <summary>
    /// Робот свободен для переезда на зарядку: простаивает либо уже стоит в парковке.
    /// </summary>
    /// <param name="machines">Состояния роботов.</param>
    /// <param name="serial">Серийный номер робота.</param>
    /// <returns><c>true</c>, если робота можно отправлять на зарядку.</returns>
    private static bool IsFreeForCharge(Dictionary<string, RobotRelocationState> machines, string serial) =>
        machines.TryGetValue(serial, out var state)
        && state is RobotRelocationState.Idle or RobotRelocationState.Parked;

    /// <summary>
    /// Робот может ехать в парковку: простаивает либо зарядился на станции.
    /// Второй случай нужен, чтобы освобождать станцию, когда зарядка одна на несколько роботов.
    /// </summary>
    /// <param name="machines">Состояния роботов.</param>
    /// <param name="snapshot">Состояние робота.</param>
    /// <param name="options">Параметры автопереездов.</param>
    /// <returns><c>true</c>, если робот может ехать в парковку.</returns>
    private static bool IsParkingCandidate(
        Dictionary<string, RobotRelocationState> machines,
        RobotSnapshot snapshot,
        ChargingOptions options)
    {
        if (!machines.TryGetValue(snapshot.SerialNumber, out var state))
        {
            return false;
        }

        return state switch
        {
            RobotRelocationState.Idle => true,
            RobotRelocationState.Charging => snapshot.BatteryCharge >= options.TargetLimit,
            _ => false,
        };
    }

    private static bool IsCharging(Dictionary<string, RobotRelocationState> machines, string serial) =>
        machines.TryGetValue(serial, out var state) && state == RobotRelocationState.Charging;
}
