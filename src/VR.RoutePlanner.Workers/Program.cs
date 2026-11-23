using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Infrastructure;
using VR.RoutePlanner.Infrastructure.Kafka;
using VR.RoutePlanner.Infrastructure.Mqtt;
using VR.RoutePlanner.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);

// Workers — единственный владелец резервов трафика: перекрываем заглушку из AddInfrastructure
// (ее получает Api) настоящим диспетчером (риск Р-2).
builder.Services.AddSingleton<ITrafficCoordinator, TrafficCoordinator>();

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<GraphWarmupWorker>();
builder.Services.AddHostedService<TrafficReservationCleanupWorker>();

var mqttOptions = builder.Configuration
    .GetSection(RoutePlannerOptions.SectionName)
    .GetSection(MqttOptions.SectionName)
    .Get<MqttOptions>() ?? new MqttOptions();

if (mqttOptions.Enabled)
{
    builder.Services.AddHostedService<MqttRobotStateWorker>();
    builder.Services.AddHostedService<RobotOrderWorker>();
}

var kafkaOptions = builder.Configuration
    .GetSection(KafkaOptions.SectionName)
    .Get<KafkaOptions>() ?? new KafkaOptions();

if (kafkaOptions.Enabled)
{
    builder.Services.AddHostedService<SimpleTaskWorker>();
}

var host = builder.Build();
host.Run();
