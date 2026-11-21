using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using VR.RoutePlanner.Application.Robots;

namespace VR.RoutePlanner.Infrastructure.Mqtt;

/// <summary>
/// Транспорт публикации через MQTTnet: подключение по <c>Routing:Mqtt</c>, QoS 1.
/// </summary>
public sealed class MqttOrderTransport : IRobotOrderTransport, IAsyncDisposable
{
    private readonly IOptions<MqttOptions> _options;
    private readonly ILogger<MqttOrderTransport> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IMqttClient? _client;

    /// <summary>
    /// Создает транспорт.
    /// </summary>
    /// <param name="options">Параметры <c>Routing:Mqtt</c>.</param>
    /// <param name="logger">Журнал.</param>
    public MqttOrderTransport(IOptions<MqttOptions> options, ILogger<MqttOrderTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishAsync(string topic, string payload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        var client = await GetClientAsync(ct).ConfigureAwait(false);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await client.PublishAsync(message, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is null)
            {
                return;
            }

            if (_client.IsConnected)
            {
                await _client.DisconnectAsync().ConfigureAwait(false);
            }

            _client.Dispose();
            _client = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<IMqttClient> GetClientAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is { IsConnected: true })
            {
                return _client;
            }

            var options = _options.Value;
            var factory = new MqttClientFactory();
            var client = factory.CreateMqttClient();
            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(options.Host, options.Port)
                .WithClientId($"{options.ClientIdPrefix}-{Environment.ProcessId}-order")
                .WithCleanSession()
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(options.KeepAliveSeconds));

            if (!string.IsNullOrWhiteSpace(options.Username))
            {
                builder = builder.WithCredentials(options.Username, options.Password);
            }

            await client.ConnectAsync(builder.Build(), ct).ConfigureAwait(false);
            _client = client;
            _logger.LogInformation("MQTT для заказов подключен: {Host}:{Port}", options.Host, options.Port);
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }
}
