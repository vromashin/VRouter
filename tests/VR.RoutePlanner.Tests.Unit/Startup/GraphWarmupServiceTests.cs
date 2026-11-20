using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VR.RoutePlanner.Application.Startup;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Tests.Unit.Support;

namespace VR.RoutePlanner.Tests.Unit.Startup;

/// <summary>
/// Оркестратор прогрева: порядок шагов, имя шага в ошибке, защита от параллельного расчета.
/// </summary>
public sealed class GraphWarmupServiceTests
{
    [Fact]
    public async Task GetLastResultAsync_IsNullBeforeFirstRun()
    {
        var service = Create(new WarmupCallLog(), new RecordingLogger<GraphWarmupService>());

        Assert.Null(await service.GetLastResultAsync(GraphFixtures.MapId, CancellationToken.None));
    }

    [Fact]
    public async Task WarmupAsync_RunsStepsInOrder_AndFillsResult()
    {
        var log = new WarmupCallLog();
        var service = Create(log, new RecordingLogger<GraphWarmupService>());

        var result = await service.WarmupAsync(GraphFixtures.MapId, CancellationToken.None);

        Assert.Equal(new[] { "graph", "mapversion", "widths", "build", "save" }, log.Calls);
        Assert.Equal(GraphFixtures.MapId, result.MapId);
        Assert.Equal(WarmupFixtures.MapVersionId, result.MapVersionId);
        Assert.Equal("2.8.1-test", result.GraphVersion);
        Assert.Equal(2, result.EdgeCount);
        Assert.Equal(2, result.ResourceCount["narrow"]);
        Assert.Equal(1, result.ResourceCount["wide"]);
        Assert.Equal(1, result.ImpassableCount["narrow"]);
        Assert.Equal(2, result.ImpassableCount["wide"]);
        Assert.False(result.ReusedWidths);
        Assert.True(result.DurationMs >= 0);
        Assert.Same(result, await service.GetLastResultAsync(GraphFixtures.MapId, CancellationToken.None));
    }

    [Fact]
    public async Task WarmupAsync_GraphFailure_WrapsStepName()
    {
        var log = new WarmupCallLog();
        var logger = new RecordingLogger<GraphWarmupService>();
        var error = new InvalidOperationException("нет активной карты");
        var service = Create(log, logger, graphError: error);

        var exception = await Assert.ThrowsAsync<WarmupStepException>(
            () => service.WarmupAsync(GraphFixtures.MapId, CancellationToken.None));

        Assert.Equal(GraphWarmupService.GraphStep, exception.Step);
        Assert.Same(error, exception.InnerException);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Error
                && entry.Message.Contains(GraphWarmupService.GraphStep, StringComparison.Ordinal));
        Assert.Null(await service.GetLastResultAsync(GraphFixtures.MapId, CancellationToken.None));
    }

    [Fact]
    public async Task WarmupAsync_SaveFailure_ReportsSaveStep()
    {
        var log = new WarmupCallLog();
        var logger = new RecordingLogger<GraphWarmupService>();
        var error = new InvalidOperationException("таблица недоступна");
        var service = Create(log, logger, saveError: error);

        var exception = await Assert.ThrowsAsync<WarmupStepException>(
            () => service.WarmupAsync(GraphFixtures.MapId, CancellationToken.None));

        Assert.Equal(GraphWarmupService.SaveStep, exception.Step);
        Assert.Same(error, exception.InnerException);
    }
    [Fact]
    public async Task WarmupAsync_ParallelCall_WaitsForSingleCalculation()
    {
        var log = new WarmupCallLog();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var graphRepository = new FakeWarehouseGraphRepository(Graph(), log) { Gate = gate };
        var service = new GraphWarmupService(
            graphRepository,
            new FakeMapVersionRepository(WarmupFixtures.MapVersionId, log),
            new FakeEdgeWidthRepository(WarmupFixtures.Widths(), reused: false, log),
            new FakeTrafficResourceBuilder(WarmupFixtures.Sets(), log),
            new FakeResourceRepository(log),
            new FakeWarmupLock(),
            new FakeWarmupStateRepository(),
            NullLogger<GraphWarmupService>.Instance);

        var first = service.WarmupAsync(GraphFixtures.MapId, CancellationToken.None);
        while (graphRepository.Calls == 0)
        {
            await Task.Yield();
        }

        var second = service.WarmupAsync(GraphFixtures.MapId, CancellationToken.None);
        gate.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, graphRepository.Calls);
        Assert.Equal(1, log.Count("graph"));
        Assert.Equal(1, log.Count("build"));
        Assert.Equal(1, log.Count("save"));
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task WarmupAsync_CompletedWhileWaiting_ReusesResultOfOtherProcess()
    {
        var log = new WarmupCallLog();
        var state = new FakeWarmupStateRepository();
        var other = WarmupFixtures.Result(GraphFixtures.MapId) with
        {
            CompletedAt = DateTimeOffset.UnixEpoch.AddMinutes(1),
        };
        var warmupLock = new FakeWarmupLock(() =>
        {
            state.Put(other);
            return Task.CompletedTask;
        });

        var service = new GraphWarmupService(
            new FakeWarehouseGraphRepository(Graph(), log),
            new FakeMapVersionRepository(WarmupFixtures.MapVersionId, log),
            new FakeEdgeWidthRepository(WarmupFixtures.Widths(), reused: false, log),
            new FakeTrafficResourceBuilder(WarmupFixtures.Sets(), log),
            new FakeResourceRepository(log),
            warmupLock,
            state,
            new RecordingLogger<GraphWarmupService>());

        var result = await service.WarmupAsync(GraphFixtures.MapId, CancellationToken.None);

        Assert.Same(other, result);
        Assert.Equal(0, log.Count("graph"));
        Assert.Equal(0, log.Count("save"));
        Assert.Equal(1, warmupLock.Acquires);
    }

    [Fact]
    public async Task WarmupAsync_ReusedWidths_ComesFromWidthCache()
    {
        var log = new WarmupCallLog();
        var service = Create(log, new RecordingLogger<GraphWarmupService>(), reused: true);

        var result = await service.WarmupAsync(GraphFixtures.MapId, CancellationToken.None);

        Assert.True(result.ReusedWidths);
    }

    [Fact]
    public async Task WarmupAsync_SecondSequentialCall_CalculatesAgain()
    {
        var log = new WarmupCallLog();
        var service = Create(log, new RecordingLogger<GraphWarmupService>());

        await service.WarmupAsync(GraphFixtures.MapId, CancellationToken.None);
        await service.WarmupAsync(GraphFixtures.MapId, CancellationToken.None);

        Assert.Equal(2, log.Count("graph"));
        Assert.Equal(2, log.Count("save"));
    }
    private static GraphWarmupService Create(
        WarmupCallLog log,
        RecordingLogger<GraphWarmupService> logger,
        Exception? graphError = null,
        Exception? saveError = null,
        bool reused = false)
    {
        return new GraphWarmupService(
            new FakeWarehouseGraphRepository(Graph(), log, graphError),
            new FakeMapVersionRepository(WarmupFixtures.MapVersionId, log),
            new FakeEdgeWidthRepository(WarmupFixtures.Widths(), reused, log),
            new FakeTrafficResourceBuilder(WarmupFixtures.Sets(), log),
            new FakeResourceRepository(log, saveError),
            new FakeWarmupLock(),
            new FakeWarmupStateRepository(),
            logger);
    }

    private static WarehouseGraph Graph()
    {
        var first = GraphFixtures.Node("wa1", 0, 0);
        var second = GraphFixtures.Node("wa2", 1, 0);
        return GraphFixtures.Graph(
            new[] { first, second },
            new[] { GraphFixtures.Edge(first, second, length: 1.0) });
    }
}