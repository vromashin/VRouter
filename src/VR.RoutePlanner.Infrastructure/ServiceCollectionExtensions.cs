using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Application.Planning;
using VR.RoutePlanner.Application.Startup;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Infrastructure.Graph;
using VR.RoutePlanner.Infrastructure.Persistence;
using VR.RoutePlanner.Infrastructure.Startup;

namespace VR.RoutePlanner.Infrastructure;

/// <summary>
/// Регистрация инфраструктурных сервисов. Наполняется по мере этапов плана.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Имя строки подключения к базе <c>rms</c>.</summary>
    public const string ConnectionStringName = "Rms";

    /// <summary>
    /// Регистрирует контекст базы <c>rms</c>, кэши, чтение графа, растр, построитель ресурсов
    /// и стартовый сервис миграций.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="configuration">Конфигурация приложения.</param>
    /// <returns>Та же коллекция сервисов.</returns>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Не задана строка подключения ConnectionStrings:{ConnectionStringName} к базе rms. "
                + "Значение задается переменной окружения ConnectionStrings__"
                + $"{ConnectionStringName} или в appsettings.Development.json локально.");
        }

        services.AddOptions<RoutePlannerOptions>()
            .Bind(configuration.GetSection(RoutePlannerOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<RoutePlannerOptions>, RoutePlannerOptionsValidator>());

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName));

        services.AddOptions<WarmupOptions>()
            .Bind(configuration.GetSection(WarmupOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<WarmupOptions>, WarmupOptionsValidator>());

        services.AddMemoryCache();

        services.AddDbContextFactory<RoutePlannerDbContext>((_, options) => options
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable(MigrationRunner.HistoryTable)));

        services.AddSingleton<IWarehouseGraphRepository, WarehouseGraphRepository>();
        services.AddSingleton<IEdgeWidthRepository, EdgeWidthRepository>();
        services.AddSingleton<IEdgePassabilityRepository, EdgePassabilityRepository>();
        services.AddSingleton<IResourceEdgeRepository, ResourceEdgeRepository>();
        services.AddSingleton<IWaitEstimator, ZeroWaitEstimator>();
        services.AddSingleton<IPathPlanner, AStarPathPlanner>();
        services.AddSingleton<IResourceRepository, ResourceRepository>();
        services.AddSingleton<IMapRasterProvider, MapRasterProvider>();
        services.AddSingleton<TrafficResourceBuilder>();
        services.AddSingleton<ITrafficResourceBuilder>(provider => provider.GetRequiredService<TrafficResourceBuilder>());
        services.AddSingleton<IMapVersionRepository, MapVersionRepository>();
        services.AddSingleton<IWarmupLock, PostgresWarmupLock>();
        services.AddSingleton<IWarmupStateRepository, WarmupStateRepository>();
        services.AddSingleton<IGraphWarmupService, GraphWarmupService>();
        services.AddHostedService<DatabaseStartupService>();

        return services;
    }
}
