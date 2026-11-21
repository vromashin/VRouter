using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Infrastructure.Mqtt;

/// <summary>
/// Клиент MQTT: подписка только на <c>APR/v2/+/{serial}/state</c> и хранение последних снимков
/// состояния в памяти. Публикаций нет: на этом шаге сервис только читает.
/// </summary>
public sealed class MqttRobotStateReader : IRobotStateReader, IAsyncDisposable
{
    /// <summary>Фильтр топика состояния: производитель и серийный номер — подстановочные.</summary>
    public const string StateTopicFilter = "APR/v2/+/+/state";

    private readonly IOptions<MqttOptions> _options;
    private readonly ILogger<MqttRobotStateReader> _logger;
    private readonly ConcurrentDictionary<string, RobotSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _payloads = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IMqttClient? _client;

    /// <summary>
    /// Создает клиент чтения состояний.
    /// </summary>
    /// <param name="options">Параметры <c>Routing:Mqtt</c>.</param>
    /// <param name="logger">Журнал.</param>
    public MqttRobotStateReader(IOptions<MqttOptions> options, ILogger<MqttRobotStateReader> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    /// <summary>Событие нового разобранного состояния робота.</summary>
    public event EventHandler<RobotSnapshot>? SnapshotReceived;

    /// <summary>Последние тела сообщений <c>state</c> по серийному номеру.</summary>
    public string? GetLatestPayload(string serialNumber) =>
        string.IsNullOrWhiteSpace(serialNumber) ? null : _payloads.GetValueOrDefault(serialNumber);

    /// <inheritdoc />
    public Task<RobotSnapshot?> GetLatestAsync(string serialNumber, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(
            string.IsNullOrWhiteSpace(serialNumber) ? null : _snapshots.GetValueOrDefault(serialNumber));
    }

    /// <inheritdoc />
    public Task<IReadOnlyCollection<RobotSnapshot>> GetAllAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyCollection<RobotSnapshot>>(_snapshots.Values.ToArray());
    }

    /// <summary>
    /// Подключается к брокеру и подписывается на состояние роботов; при обрыве переподключается.
    /// </summary>
    /// <param name="ct">Токен остановки.</param>
    /// <returns>Задача цикла подключения.</returns>
    public async Task RunAsync(CancellationToken ct)
    {
        var options = _options.Value;
        var delay = TimeSpan.FromSeconds(Math.Min(1, options.ReconnectMaxSeconds));
        var maxDelay = TimeSpan.FromSeconds(options.ReconnectMaxSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndWaitAsync(options, ct).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(1, options.ReconnectMaxSeconds));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "MQTT {Host}:{Port} недоступен, повтор через {Delay:F0} с",
                    options.Host,
                    options.Port,
                    delay.TotalSeconds);
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = delay < maxDelay ? TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks)) : maxDelay;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task ConnectAndWaitAsync(MqttOptions options, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null && _client.IsConnected)
            {
                return;
            }

            var factory = new MqttClientFactory();
            var client = factory.CreateMqttClient();
            client.ApplicationMessageReceivedAsync += OnMessageAsync;

            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(options.Host, options.Port)
                .WithClientId($"{options.ClientIdPrefix}-{Environment.ProcessId}")
                .WithCleanSession()
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(options.KeepAliveSeconds));

            if (!string.IsNullOrWhiteSpace(options.Username))
            {
                builder = builder.WithCredentials(options.Username, options.Password);
            }

            var clientOptions = builder.Build();
            await client.ConnectAsync(clientOptions, ct).ConfigureAwait(false);

            var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(StateTopicFilter)
                .Build();
            await client.SubscribeAsync(subscribeOptions, ct).ConfigureAwait(false);

            _client = client;
            _logger.LogInformation(
                "MQTT подключен: {Host}:{Port}, подписка {Topic}",
                options.Host,
                options.Port,
                StateTopicFilter);

            while (!ct.IsCancellationRequested && client.IsConnected)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }

            if (!client.IsConnected)
            {
                _logger.LogWarning("MQTT: соединение с {Host}:{Port} потеряно", options.Host, options.Port);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Обрабатывает полученное сообщение <c>state</c>: разбирает и запоминает снимок.
    /// Битое сообщение не сохраняется и попадает в журнал предупреждением.
    /// </summary>
    /// <param name="topic">Топик сообщения.</param>
    /// <param name="payload">Тело сообщения в UTF-8.</param>
    /// <returns><c>true</c>, если снимок сохранен.</returns>
    public bool ProcessPayload(string topic, string? payload)
    {
        if (!RobotStateParser.TryParse(payload, out var snapshot, out var error) || snapshot is null)
        {
            _logger.LogWarning("MQTT: сообщение {Topic} не разобрано: {Error}", topic, error);
            return false;
        }

        _snapshots[snapshot.SerialNumber] = snapshot;
        if (payload is not null)
        {
            _payloads[snapshot.SerialNumber] = payload;
        }

        _logger.LogDebug("MQTT: состояние {State}", RobotStateParser.Describe(snapshot));
        SnapshotReceived?.Invoke(this, snapshot);
        return true;
    }

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var payload = args.ApplicationMessage.Payload.Length == 0
            ? null
            : Encoding.UTF8.GetString(System.Buffers.BuffersExtensions.ToArray(args.ApplicationMessage.Payload));

        ProcessPayload(args.ApplicationMessage.Topic, payload);
        return Task.CompletedTask;
    }

    private async Task DisconnectAsync()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "MQTT: не удалось корректно отключиться");
        }
        finally
        {
            _client.ApplicationMessageReceivedAsync -= OnMessageAsync;
            _client.Dispose();
            _client = null;
        }
    }
}
