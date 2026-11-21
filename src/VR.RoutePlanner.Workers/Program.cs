using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Infrastructure;
using VR.RoutePlanner.Infrastructure.Mqtt;
using VR.RoutePlanner.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<GraphWarmupWorker>();

var mqttOptions = builder.Configuration
    .GetSection(RoutePlannerOptions.SectionName)
    .GetSection(MqttOptions.SectionName)
    .Get<MqttOptions>() ?? new MqttOptions();

if (mqttOptions.Enabled)
{
    builder.Services.AddHostedService<MqttRobotStateWorker>();
    builder.Services.AddHostedService<RobotOrderWorker>();
}

var host = builder.Build();
host.Run();
