using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using VR.RoutePlanner.Infrastructure.Startup;
using VR.RoutePlanner.Tests.Integration.Support;
using Xunit;

namespace VR.RoutePlanner.Tests.Integration.Startup;

/// <summary>
/// Блокировка прогрева на двух настоящих соединениях: второй процесс ждет первого,
/// отмена и таймаут не оставляют за собой захваченный advisory lock.
/// Без переменной окружения <c>VRP_RMS_CONNECTION</c> тесты пропускаются.
/// </summary>
public sealed class PostgresWarmupLockTests
{
    private static readonly TimeSpan WaitForOther = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [SkippableFact]
    public async Task AcquireAsync_SecondSessionWaitsForFirst()
    {
        var lockOwner = CreateLock(WaitForOther);
        var mapId = NewMapId();

        await using var first = await lockOwner.AcquireAsync(mapId, CancellationToken.None);
        var second = CreateLock(WaitForOther).AcquireAsync(mapId, CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(second.IsCompleted, "второй процесс взял блокировку, пока ее держит первый");

        await first.DisposeAsync();

        await using var secondLease = await second.WaitAsync(Patience);
        Assert.NotNull(secondLease);
    }

    [SkippableFact]
    public async Task AcquireAsync_CanceledWaiting_LeavesNoLock()
    {
        var mapId = NewMapId();
        await using var first = await CreateLock(WaitForOther).AcquireAsync(mapId, CancellationToken.None);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateLock(WaitForOther).AcquireAsync(mapId, cancellation.Token));

        await first.DisposeAsync();

        // Если отмененный захват оставил сессионную блокировку, повторный захват повиснет.
        var stopwatch = Stopwatch.StartNew();
        await using var afterCancel = await CreateLock(TimeSpan.FromSeconds(10))
            .AcquireAsync(mapId, CancellationToken.None)
            .WaitAsync(Patience);

        Assert.NotNull(afterCancel);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"повторный захват занял {stopwatch.Elapsed}.");
    }

    [SkippableFact]
    public async Task AcquireAsync_Timeout_ThrowsTimeoutException()
    {
        var mapId = NewMapId();
        await using var first = await CreateLock(WaitForOther).AcquireAsync(mapId, CancellationToken.None);

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(
            () => CreateLock(TimeSpan.FromMilliseconds(300)).AcquireAsync(mapId, CancellationToken.None));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"ожидание таймаута заняло {stopwatch.Elapsed}.");

        await first.DisposeAsync();

        await using var afterTimeout = await CreateLock(WaitForOther)
            .AcquireAsync(mapId, CancellationToken.None)
            .WaitAsync(Patience);

        Assert.NotNull(afterTimeout);
    }

    private static PostgresWarmupLock CreateLock(TimeSpan waitTimeout) => new(
        new TestDbContextFactory(TestDatabase.BuildOptions(TestDatabase.RequireConnection())),
        NullLogger<PostgresWarmupLock>.Instance,
        waitTimeout);

    private static string NewMapId() => $"lock-test-{Guid.NewGuid():N}";
}
