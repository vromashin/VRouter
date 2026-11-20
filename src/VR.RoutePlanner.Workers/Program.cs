using VR.RoutePlanner.Infrastructure;
using VR.RoutePlanner.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<GraphWarmupWorker>();

var host = builder.Build();
host.Run();
