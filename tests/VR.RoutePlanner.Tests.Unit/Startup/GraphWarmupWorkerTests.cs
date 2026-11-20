using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Startup;
using VR.RoutePlanner.Tests.Unit.Support;
using VR.RoutePlanner.Workers;

namespace VR.RoutePlanner.Tests.Unit.Startup;

/// <summary>
/// Проверки хостед-сервиса прогрева: политика FailFast и поведение при отключенном прогреве.
/// </summary>
public sealed class GraphWarmupWorkerTests
{
    [Fact]
    public async Task RunOnceAsync_WithoutFailFast_DoesNotThrow()
    {
        var error = new WarmupStepException(GraphWarmupService.WidthsStep, new InvalidOperationException("нет растра"));
        var service = new FakeWarmupService(error);
        var worker = Create(service, new WarmupOptions { FailFast = false });

        var completed = await worker.RunOnceAsync(CancellationToken.None);

        Assert.False(completed);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task RunOnceAsync_WithFailFast_RethrowsStepError()
    {
        var error = new WarmupStepException(GraphWarmupService.WidthsStep, new InvalidOperationException("нет растра"));
        var service = new FakeWarmupService(error);
        var worker = Create(service, new WarmupOptions { FailFast = true });

        var thrown = await Assert.ThrowsAsync<WarmupStepException>(
            () => worker.RunOnceAsync(CancellationToken.None));

        Assert.Same(error, thrown);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_DoesNotWarmUp()
    {
        var service = new FakeWarmupService();
        var worker = Create(service, new WarmupOptions { Enabled = false });

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutRefreshInterval_RunsOnceOnStartup()
    {
        var service = new FakeWarmupService();
        var worker = Create(
            service,
            new WarmupOptions { RunOnStartup = true, RefreshIntervalMinutes = 0 });

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, service.Calls);
    }

    private static GraphWarmupWorker Create(IGraphWarmupService service, WarmupOptions options) =>
        new(service, Options.Create(options), NullLogger<GraphWarmupWorker>.Instance);
}