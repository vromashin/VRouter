using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Application.Graph;
using VR.RoutePlanner.Application.Startup;
using VR.RoutePlanner.Application.Traffic;
using VR.RoutePlanner.Domain.Graph;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Tests.Unit.Support;

/// <summary>
/// Журнал вызовов шагов прогрева: позволяет проверить порядок и число прогонов.
/// </summary>
internal sealed class WarmupCallLog
{
    private readonly List<string> _calls = new();
    private readonly object _sync = new();

    public void Add(string step)
    {
        lock (_sync)
        {
            _calls.Add(step);
        }
    }

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_sync)
            {
                return _calls.ToArray();
            }
        }
    }

    public int Count(string step) => Calls.Count(item => item == step);
}

/// <summary>
/// Подставное чтение графа: считает вызовы, умеет блокироваться и падать.
/// </summary>
internal sealed class FakeWarehouseGraphRepository : IWarehouseGraphRepository
{
    private readonly WarehouseGraph _graph;
    private readonly WarmupCallLog _log;
    private readonly Exception? _error;
    private int _calls;

    public FakeWarehouseGraphRepository(WarehouseGraph graph, WarmupCallLog log, Exception? error = null)
    {
        _graph = graph;
        _log = log;
        _error = error;
    }

    /// <summary>Ворота, которые держат загрузку графа до сигнала из теста.</summary>
    public TaskCompletionSource? Gate { get; init; }

    public int Calls => Volatile.Read(ref _calls);

    public async Task<WarehouseGraph> LoadAsync(string mapId, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        _log.Add("graph");

        if (Gate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }

        if (_error is not null)
        {
            throw _error;
        }

        return _graph;
    }

    public Task<string> GetGraphVersionAsync(string mapId, CancellationToken ct) => Task.FromResult(_graph.GraphVersion);

    public Task InvalidateAsync(string mapId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Подставная версия карты: всегда возвращает заданный идентификатор.
/// </summary>
internal sealed class FakeMapVersionRepository : IMapVersionRepository
{
    private readonly Guid _mapVersionId;
    private readonly WarmupCallLog _log;
    private readonly Exception? _error;

    public FakeMapVersionRepository(Guid mapVersionId, WarmupCallLog log, Exception? error = null)
    {
        _mapVersionId = mapVersionId;
        _log = log;
        _error = error;
    }

    public Task<Guid> GetOrCreateAsync(string mapId, WarehouseGraph graph, CancellationToken ct)
    {
        _log.Add("mapversion");

        if (_error is not null)
        {
            throw _error;
        }

        return Task.FromResult(_mapVersionId);
    }
}

/// <summary>
/// Подставные ширины ребер: отдает заданный набор и признак попадания в кэш.
/// </summary>
internal sealed class FakeEdgeWidthRepository : IEdgeWidthRepository
{
    private readonly IReadOnlyList<EdgeWidth> _widths;
    private readonly bool _reused;
    private readonly WarmupCallLog _log;
    private readonly Exception? _error;

    public FakeEdgeWidthRepository(
        IReadOnlyList<EdgeWidth> widths,
        bool reused,
        WarmupCallLog log,
        Exception? error = null)
    {
        _widths = widths;
        _reused = reused;
        _log = log;
        _error = error;
    }

    public Task<EdgeWidthLoad> GetOrCreateAsync(
        Guid mapVersionId,
        WarehouseGraph graph,
        string mapId,
        CancellationToken ct)
    {
        _log.Add("widths");

        if (_error is not null)
        {
            throw _error;
        }

        return Task.FromResult(new EdgeWidthLoad(_widths, _reused));
    }
}

/// <summary>
/// Подставной построитель ресурсов: отдает заранее заданные наборы профилей.
/// </summary>
internal sealed class FakeTrafficResourceBuilder : ITrafficResourceBuilder
{
    private readonly IReadOnlyList<ProfileResourceSet> _sets;
    private readonly WarmupCallLog _log;
    private readonly Exception? _error;

    public FakeTrafficResourceBuilder(
        IReadOnlyList<ProfileResourceSet> sets,
        WarmupCallLog log,
        WidthSource widthSource = WidthSource.Raster,
        Exception? error = null)
    {
        _sets = sets;
        _log = log;
        WidthSource = widthSource;
        _error = error;
    }

    public WidthSource WidthSource { get; }

    public Task<IReadOnlyList<ProfileResourceSet>> BuildAsync(
        WarehouseGraph graph,
        Guid mapVersionId,
        CancellationToken ct = default)
    {
        _log.Add("build");
        return Build();
    }

    public Task<IReadOnlyList<ProfileResourceSet>> BuildAsync(
        WarehouseGraph graph,
        IReadOnlyList<EdgeWidth> widths,
        Guid mapVersionId,
        CancellationToken ct = default)
    {
        _log.Add("build");
        return Build();
    }

    private Task<IReadOnlyList<ProfileResourceSet>> Build()
    {
        if (_error is not null)
        {
            throw _error;
        }

        return Task.FromResult(_sets);
    }
}
/// <summary>
/// Подставная запись ресурсов: считает вызовы и умеет падать.
/// </summary>
internal sealed class FakeResourceRepository : IResourceRepository
{
    private readonly WarmupCallLog _log;
    private readonly Exception? _error;

    public FakeResourceRepository(WarmupCallLog log, Exception? error = null)
    {
        _log = log;
        _error = error;
    }

    public IReadOnlyList<ProfileResourceSet> Saved { get; private set; } = Array.Empty<ProfileResourceSet>();

    public Task SaveAsync(Guid mapVersionId, IReadOnlyList<ProfileResourceSet> sets, CancellationToken ct)
    {
        _log.Add("save");

        if (_error is not null)
        {
            throw _error;
        }

        Saved = sets;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Подставной оркестратор прогрева для тестов хостед-сервиса.
/// </summary>
internal sealed class FakeWarmupService : IGraphWarmupService
{
    private readonly Exception? _error;
    private WarmupResult? _lastResult;

    public FakeWarmupService(Exception? error = null)
    {
        _error = error;
    }

    public int Calls { get; private set; }

    public WarmupResult? LastResult => _lastResult;

    public Task<WarmupResult?> GetLastResultAsync(string mapId, CancellationToken ct)
    {
        return Task.FromResult(_lastResult);
    }

    public Task<WarmupResult> WarmupAsync(string mapId, CancellationToken ct)
    {
        Calls++;

        if (_error is not null)
        {
            throw _error;
        }

        _lastResult = WarmupFixtures.Result(mapId);
        return Task.FromResult(_lastResult);
    }
}

/// <summary>
/// Подставная блокировка прогрева: сериализует вызовы внутри процесса тестов
/// и считает захваты.
/// </summary>
internal sealed class FakeWarmupLock : IWarmupLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<Task>? _onAcquire;

    public FakeWarmupLock(Func<Task>? onAcquire = null)
    {
        _onAcquire = onAcquire;
    }

    public int Acquires { get; private set; }

    public async Task<IAsyncDisposable> AcquireAsync(string mapId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        Acquires++;

        if (_onAcquire is not null)
        {
            await _onAcquire().ConfigureAwait(false);
        }

        return new Lease(_gate);
    }

    private sealed class Lease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate;
        private bool _released;

        public Lease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public ValueTask DisposeAsync()
        {
            if (!_released)
            {
                _released = true;
                _gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Подставное хранилище итога прогрева: словарь в памяти процесса.</summary>
internal sealed class FakeWarmupStateRepository : IWarmupStateRepository
{
    private readonly Dictionary<string, WarmupResult> _results = new(StringComparer.Ordinal);

    public void Put(WarmupResult result)
    {
        _results[result.MapId] = result;
    }

    public Task<WarmupResult?> GetAsync(string mapId, CancellationToken ct)
    {
        return Task.FromResult(_results.TryGetValue(mapId, out var result) ? result : null);
    }

    public Task SaveAsync(WarmupResult result, CancellationToken ct)
    {
        Put(result);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Ширины, наборы профилей и итог прогрева для тестов без базы.
/// </summary>
internal static class WarmupFixtures
{
    public static readonly Guid MapVersionId = new("22222222-2222-2222-2222-222222222222");

    public static IReadOnlyList<EdgeWidth> Widths()
    {
        return new[]
        {
            new EdgeWidth(MapVersionId, EdgeId(1), 1.5, 1.0, 2, WidthSource.Raster, "raster.png"),
            new EdgeWidth(MapVersionId, EdgeId(2), 2.5, 1.0, 2, WidthSource.Raster, "raster.png"),
        };
    }

    public static IReadOnlyList<ProfileResourceSet> Sets()
    {
        var narrowResources = new[]
        {
            Resource("801-narrow-C1-1", "narrow"),
            Resource("801-narrow-C1-2", "narrow"),
        };

        var wideResources = new[]
        {
            Resource("801-wide-C1-1", "wide"),
        };

        var narrowPassability = new[]
        {
            Passability(1, "narrow", isPassable: true),
            Passability(2, "narrow", isPassable: false),
        };

        var widePassability = new[]
        {
            Passability(1, "wide", isPassable: false),
            Passability(2, "wide", isPassable: false),
        };

        return new[]
        {
            new ProfileResourceSet(
                Profile("narrow", 0.9),
                narrowResources,
                Array.Empty<TrafficResourceEdge>(),
                Array.Empty<TrafficResourceNode>(),
                Array.Empty<ResourceConflict>(),
                narrowPassability),
            new ProfileResourceSet(
                Profile("wide", 2.1),
                wideResources,
                Array.Empty<TrafficResourceEdge>(),
                Array.Empty<TrafficResourceNode>(),
                Array.Empty<ResourceConflict>(),
                widePassability),
        };
    }

    public static WarmupResult Result(string mapId) => new(
        mapId,
        MapVersionId,
        "2.8.1-test",
        2,
        WidthSource.Raster,
        new Dictionary<string, int>(StringComparer.Ordinal) { ["narrow"] = 2, ["wide"] = 1 },
        new Dictionary<string, int>(StringComparer.Ordinal) { ["narrow"] = 1, ["wide"] = 2 },
        5,
        false,
        DateTimeOffset.UnixEpoch);

    public static AgvProfile Profile(string name, double width) => new(
        name,
        width,
        1.455,
        0.17);

    private static TrafficResource Resource(string code, string profile)
    {
        return new TrafficResource(
            StableId(code),
            MapVersionId,
            code,
            profile,
            TrafficResourceType.EXCLUSIVE,
            Capacity: 1,
            MinEntryInterval: 0,
            MaxOccupancyTime: 0,
            IsActive: true);
    }

    private static EdgePassability Passability(int edge, string profile, bool isPassable)
    {
        return new EdgePassability(
            MapVersionId,
            EdgeId(edge),
            profile,
            isPassable,
            CorridorClass.C1,
            isPassable ? null : "узко");
    }

    private static Guid EdgeId(int value)
    {
        var bytes = new byte[16];
        bytes[0] = (byte)value;
        bytes[15] = 1;
        return new Guid(bytes);
    }

    private static Guid StableId(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, bytes.Length);
        return new Guid(bytes);
    }
}