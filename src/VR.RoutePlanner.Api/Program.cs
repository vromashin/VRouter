using VR.RoutePlanner.Infrastructure;
using VR.RoutePlanner.Infrastructure.Mqtt;
using VR.RoutePlanner.Api.Hosted;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Карта опрашивает /api/Visualization/rawVisualizationAll, поэтому процесс API
// тоже держит MQTT-подписку на visualization: данные нужны в его памяти.
var mqttOptions = builder.Configuration
    .GetSection("Routing:Mqtt")
    .Get<MqttOptions>() ?? new MqttOptions();

if (mqttOptions.Enabled)
{
    builder.Services.AddHostedService<ApiMqttReaderService>();
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();

app.Run();
