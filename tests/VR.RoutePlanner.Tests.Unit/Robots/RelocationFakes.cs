using Microsoft.Extensions.Logging.Abstractions;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Robots;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Tests.Unit.Planning;

namespace VR.RoutePlanner.Tests.Unit.Robots;

/// <summary>
/// Подставной публикатор событий роботов: запоминает тело и топик, умеет падать по требованию.
/// </summary>
internal sealed class FakeRobotEventPublisher : IRobotEventPublisher
{
    /// <summary>Опубликованные события в порядке отправки.</summary>
    public List<(RobotChargingCompletedEvent Payload, string Topic)> Published { get; } = [];

    /// <summary>Ошибка, которую публикатор бросает вместо отправки.</summary>
    public Exception? Error { get; set; }

    /// <inheritdoc />
    public Task PublishChargingCompletedAsync(
        RobotChargingCompletedEvent payload,
        string topic,
        CancellationToken ct)
    {
        if (Error is not null)
        {
            throw Error;
        }

        Published.Add((payload, topic));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Сборка карты с узлами <c>CHARGING</c>/<c>PARKING</c> и сервисов автопереезда для тестов:
/// база, MQTT и Kafka не используются.
/// </summary>
internal static class RelocationFixtures
{
    public const string Serial = "98001";
    public const string OtherSerial = "98002";
    public const string ThirdSerial = "98003";

    /// <summary>
    /// Карта: два узла-стоянки, зарядки и парковки. Координаты подобраны так, чтобы ближайшая
    /// зарядка от <c>wa100</c> была <c>ch1</c>, а от <c>wa101</c> — <c>ch2</c>.
    /// </summary>
    /// <param name="chargingCount">Сколько узлов <c>CHARGING</c> на карте.</param>
    /// <param name="parkingCount">Сколько узлов <c>PARKING</c> на карте.</param>
    public static WarehouseGraph Graph(int chargingCount = 2, int parkingCount = 2)
    {
        var nodes = new List<WarehouseNode>
        {
            OrderFixtures.Node("wa100", 0, 0),
            OrderFixtures.Node("wa101", 0, 20),
        };

        if (chargingCount > 0)
        {
            nodes.Add(OrderFixtures.Node("ch1", 5, 0, RelocationNodeTypes.Charging));
        }

        if (chargingCount > 1)
        {
            nodes.Add(OrderFixtures.Node("ch2", 5, 20, RelocationNodeTypes.Charging));
        }

        if (parkingCount > 0)
        {
            nodes.Add(OrderFixtures.Node("pk1", 10, 0, RelocationNodeTypes.Parking));
        }

        if (parkingCount > 1)
        {
            nodes.Add(OrderFixtures.Node("pk2", 10, 20, RelocationNodeTypes.Parking));
        }

        return OrderFixtures.Graph(nodes, []);
    }

    /// <summary>Состояние робота для тестов автопереезда.</summary>
    public static RobotSnapshot Snapshot(
        string serial,
        double battery = 80,
        bool charging = false,
        bool driving = false,
        string? orderId = null,
        string? lastNodeId = "wa100",
        double x = 0,
        double y = 0,
        IEnumerable<string>? errors = null) =>
        new(
            serial,
            "ONLINE",
            driving,
            orderId,
            0,
            x,
            y,
            0,
            OrderFixtures.MapId,
            battery,
            errors?.ToList() ?? [],
            [],
            new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero),
            "2.1.0",
            lastNodeId,
            null,
            false,
            null,
            null,
            charging);

    /// <summary>Заказ робота для подстановки в хранилище.</summary>
    public static Domain.Robots.RobotOrder Order(
        string serial,
        string orderId,
        string status,
        string? targetNodeId) =>
        new(
            orderId,
            serial,
            0,
            status,
            "{}",
            0,
            targetNodeId,
            null,
            new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero),
            null,
            null,
            null,
            null,
            []);

    /// <summary>Все зависимости сервиса автопереезда и тестовые подстановки.</summary>
    public sealed record Harness(
        RobotRelocationService Service,
        RobotOrderService Orders,
        FakeOrderRepository Repository,
        FakeStateReader States,
        FakePublisher Publisher,
        FakeRobotEventPublisher Events,
        RecordingComposer Composer,
        TestTimeProvider Time,
        ChargingOptions Charging);

    /// <summary>Создает сервис автопереезда на подстановках.</summary>
    public static Harness Create(
        WarehouseGraph? graph = null,
        ChargingOptions? charging = null,
        RoutePlannerOptions? routing = null)
    {
        var map = graph ?? Graph();
        var publisher = new FakePublisher();
        var composer = new RecordingComposer();
        var created = RobotOrderServiceTests.CreateWithTraffic(
            graph: map,
            publisher: publisher,
            composer: composer);
        var events = new FakeRobotEventPublisher();
        var chargingOptions = charging ?? new ChargingOptions();
        var routingOptions = routing ?? PlanningFixtures.Options();

        var service = new RobotRelocationService(
            new FakeGraphRepository(map),
            created.Orders,
            created.States,
            created.Service,
            events,
            new FixedOptions<ChargingOptions>(chargingOptions),
            new FixedOptions<RoutePlannerOptions>(routingOptions),
            created.Time,
            NullLogger<RobotRelocationService>.Instance);

        return new Harness(
            service,
            created.Service,
            created.Orders,
            created.States,
            publisher,
            events,
            composer,
            created.Time,
            chargingOptions);
    }
}

/// <summary>
/// Сборщик заказа, который запоминает запросы и отдает тело как <see cref="FakeComposer"/>.
/// </summary>
internal sealed class RecordingComposer : IVdaOrderComposer
{
    private readonly FakeComposer _inner = new();

    /// <summary>Запросы сборки в порядке вызовов.</summary>
    public List<VdaOrderRequest> Requests { get; } = [];

    /// <inheritdoc />
    public string Compose(VdaOrderRequest request)
    {
        Requests.Add(request);
        return _inner.Compose(request);
    }
}
