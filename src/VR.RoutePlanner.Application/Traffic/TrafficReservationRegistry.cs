using Microsoft.Extensions.Options;
using VR.RoutePlanner.Application.Configuration;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Application.Traffic;

/// <summary>
/// Реестр резервов транспортных ресурсов: singleton, потокобезопасный.
///
/// Ключ резерва — <c>(vrt_MapVersion.Id, vrt_TrafficResource.Id)</c>, владелец — серийный номер
/// робота. Резервы живут только в памяти процесса; <c>vrt_*</c> их не хранит.
///
/// Набор захватывается атомарно: при отказе хотя бы на одном ресурсе не создается ни одного
/// резерва. Блокировка одна на реестр: резервов единицы и все операции короткие, поэтому
/// порядок захвата ключей и дедлок не возникают.
/// </summary>
public sealed class TrafficReservationRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid MapVersionId, Guid ResourceId), List<TrafficReservation>> _byResource = [];
    private readonly Dictionary<string, HashSet<(Guid MapVersionId, Guid ResourceId)>> _byOwner =
        new(StringComparer.Ordinal);

    private readonly IOptions<RoutePlannerOptions> _options;

    /// <summary>
    /// Создает реестр резервов.
    /// </summary>
    /// <param name="options">Параметры <c>Routing</c>: TTL резерва и грейс после проезда.</param>
    public TrafficReservationRegistry(IOptions<RoutePlannerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Атомарно захватывает набор ресурсов за роботом.
    /// </summary>
    /// <param name="ownerSerial">Серийный номер робота.</param>
    /// <param name="mapVersionId">Версия карты.</param>
    /// <param name="entries">Ресурсы горизонта.</param>
    /// <param name="now">Текущий момент.</param>
    /// <returns>Принятый итог либо отказ с мешающим ресурсом.</returns>
    public TrafficReservationResult TryAcquire(
        string ownerSerial,
        Guid mapVersionId,
        IReadOnlyList<TrafficHorizonEntry> entries,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSerial);
        ArgumentNullException.ThrowIfNull(entries);

        lock (_gate)
        {
            var blocked = FindBlocked(ownerSerial, mapVersionId, entries);
            if (blocked is not null)
            {
                return blocked;
            }

            foreach (var entry in entries)
            {
                if (!IsReservable(entry))
                {
                    continue;
                }

                Save(ownerSerial, mapVersionId, entry, now);
            }

            return TrafficReservationResult.Accept();
        }
    }

    /// <summary>
    /// Продлевает горизонт по новому <c>state</c>: что осталось в горизонте — <c>RenewedAt = now</c>,
    /// что робот проехал — <c>PassedAt = now</c>, недостающее добирается best-effort.
    /// Отказ на отдельном ресурсе не отменяет уже взятые и не рвет заказ.
    /// </summary>
    /// <param name="ownerSerial">Серийный номер робота.</param>
    /// <param name="mapVersionId">Версия карты.</param>
    /// <param name="horizon">Новый горизонт робота.</param>
    /// <param name="now">Текущий момент.</param>
    /// <returns>Принятый итог либо первый отказ по недобранному ресурсу.</returns>
    public TrafficReservationResult Renew(
        string ownerSerial,
        Guid mapVersionId,
        IReadOnlyList<TrafficHorizonEntry> horizon,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSerial);
        ArgumentNullException.ThrowIfNull(horizon);

        var inHorizon = horizon
            .Where(IsReservable)
            .Select(entry => entry.ResourceId)
            .ToHashSet();

        lock (_gate)
        {
            if (_byOwner.TryGetValue(ownerSerial, out var owned))
            {
                foreach (var key in owned.ToList())
                {
                    var reservations = _byResource[key];
                    for (var index = 0; index < reservations.Count; index++)
                    {
                        var reservation = reservations[index];
                        if (!inHorizon.Contains(reservation.ResourceId))
                        {
                            if (reservation.PassedAt is null)
                            {
                                reservations[index] = reservation with { PassedAt = now };
                            }

                            continue;
                        }

                        reservations[index] = reservation with
                        {
                            RenewedAt = now,
                            PassedAt = null,
                        };
                    }
                }
            }

            TrafficReservationResult? refusal = null;
            foreach (var entry in horizon)
            {
                if (!IsReservable(entry) || IsHeldBy(ownerSerial, mapVersionId, entry.ResourceId))
                {
                    continue;
                }

                var blocked = FindBlocked(ownerSerial, mapVersionId, [entry]);
                if (blocked is not null)
                {
                    refusal ??= blocked;
                    continue;
                }

                Save(ownerSerial, mapVersionId, entry, now);
            }

            return refusal ?? TrafficReservationResult.Accept();
        }
    }

    /// <summary>
    /// Снимает все резервы робота.
    /// </summary>
    /// <param name="ownerSerial">Серийный номер робота.</param>
    /// <returns>Сколько резервов снято.</returns>
    public int Release(string ownerSerial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSerial);

        lock (_gate)
        {
            if (!_byOwner.TryGetValue(ownerSerial, out var owned))
            {
                return 0;
            }

            var removed = 0;
            foreach (var key in owned)
            {
                if (!_byResource.TryGetValue(key, out var reservations))
                {
                    continue;
                }

                removed += reservations.RemoveAll(item =>
                    string.Equals(item.OwnerSerial, ownerSerial, StringComparison.Ordinal));
                if (reservations.Count == 0)
                {
                    _byResource.Remove(key);
                }
            }

            _byOwner.Remove(ownerSerial);
            return removed;
        }
    }

    /// <summary>
    /// Снимает просроченные резервы: не подтвержденные дольше <c>ReservationHorizonSeconds</c>
    /// и проеханные дольше <c>ReleaseHorizonSeconds</c>.
    /// </summary>
    /// <param name="now">Текущий момент.</param>
    /// <returns>Снятые резервы.</returns>
    public IReadOnlyList<TrafficReservation> Sweep(DateTimeOffset now)
    {
        var options = _options.Value;
        var ttl = TimeSpan.FromSeconds(Math.Max(0, options.ReservationHorizonSeconds));
        var grace = TimeSpan.FromSeconds(Math.Max(0, options.ReleaseHorizonSeconds));

        lock (_gate)
        {
            var removed = new List<TrafficReservation>();
            foreach (var key in _byResource.Keys.ToList())
            {
                var reservations = _byResource[key];
                for (var index = reservations.Count - 1; index >= 0; index--)
                {
                    var reservation = reservations[index];
                    var stale = now - reservation.RenewedAt > ttl;
                    var passed = reservation.PassedAt is { } passedAt && now - passedAt > grace;
                    if (!stale && !passed)
                    {
                        continue;
                    }

                    removed.Add(reservation);
                    reservations.RemoveAt(index);
                    Forget(reservation.OwnerSerial, key);
                }

                if (reservations.Count == 0)
                {
                    _byResource.Remove(key);
                }
            }

            return removed;
        }
    }

    /// <summary>
    /// Отдает резервы робота: нужно журналу и тестам.
    /// </summary>
    /// <param name="ownerSerial">Серийный номер робота.</param>
    /// <returns>Резервы робота в порядке захвата.</returns>
    public IReadOnlyList<TrafficReservation> GetOwnerReservations(string ownerSerial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSerial);

        lock (_gate)
        {
            if (!_byOwner.TryGetValue(ownerSerial, out var owned))
            {
                return [];
            }

            return owned
                .SelectMany(key => _byResource[key])
                .Where(item => string.Equals(item.OwnerSerial, ownerSerial, StringComparison.Ordinal))
                .OrderBy(item => item.AcquiredAt)
                .ThenBy(item => item.ResourceCode, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// Ресурс резервируется: коридоры <c>EXCLUSIVE</c>/<c>DIRECTIONAL</c>/<c>PARALLEL</c>
    /// и узловые <c>CONFLICT_ZONE</c>. Узловой <c>EXCLUSIVE</c> и <c>INDEPENDENT</c> — нет.
    /// </summary>
    private static bool IsReservable(TrafficHorizonEntry entry) =>
        entry.ResourceType != TrafficResourceType.INDEPENDENT
        && !(entry.IsNode && entry.ResourceType == TrafficResourceType.EXCLUSIVE);

    private bool IsHeldBy(string ownerSerial, Guid mapVersionId, Guid resourceId) =>
        _byResource.TryGetValue((mapVersionId, resourceId), out var reservations)
        && reservations.Any(item => string.Equals(item.OwnerSerial, ownerSerial, StringComparison.Ordinal));

    private TrafficReservationResult? FindBlocked(
        string ownerSerial,
        Guid mapVersionId,
        IEnumerable<TrafficHorizonEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!IsReservable(entry)
                || !_byResource.TryGetValue((mapVersionId, entry.ResourceId), out var reservations))
            {
                continue;
            }

            var others = reservations
                .Where(item => !string.Equals(item.OwnerSerial, ownerSerial, StringComparison.Ordinal))
                .ToList();

            if (others.Count == 0)
            {
                continue;
            }

            var blocked = entry.ResourceType switch
            {
                // EXCLUSIVE и CONFLICT_ZONE: Capacity из строки ресурса (у узловых = 1).
                TrafficResourceType.EXCLUSIVE or TrafficResourceType.CONFLICT_ZONE =>
                    others.Count >= Math.Max(1, entry.Capacity) ? others[0] : null,

                // DIRECTIONAL: один владелец и только в его направлении, Capacity не расширяет.
                TrafficResourceType.DIRECTIONAL => others[0],

                // PARALLEL: до Capacity владельцев, все в одном направлении.
                TrafficResourceType.PARALLEL =>
                    others.FirstOrDefault(item => item.Direction != entry.Direction)
                    ?? (others.Count >= Math.Max(1, entry.Capacity) ? others[0] : null),

                _ => null,
            };

            if (blocked is not null)
            {
                return TrafficReservationResult.Blocked(
                    blocked.OwnerSerial,
                    blocked.ResourceId,
                    blocked.ResourceCode);
            }
        }

        return null;
    }

    private void Save(
        string ownerSerial,
        Guid mapVersionId,
        TrafficHorizonEntry entry,
        DateTimeOffset now)
    {
        var key = (mapVersionId, entry.ResourceId);
        if (!_byResource.TryGetValue(key, out var reservations))
        {
            reservations = [];
            _byResource[key] = reservations;
        }

        var existing = reservations.FindIndex(item =>
            string.Equals(item.OwnerSerial, ownerSerial, StringComparison.Ordinal));

        if (existing >= 0)
        {
            // Повторный захват тем же владельцем идемпотентен: владелец не дублируется.
            reservations[existing] = reservations[existing] with
            {
                RenewedAt = now,
                PassedAt = null,
            };
        }
        else
        {
            reservations.Add(new TrafficReservation(
                mapVersionId,
                entry.ResourceId,
                entry.ResourceCode,
                ownerSerial,
                entry.Direction,
                entry.ResourceType,
                entry.Capacity,
                now,
                now,
                PassedAt: null));
        }

        if (!_byOwner.TryGetValue(ownerSerial, out var owned))
        {
            owned = [];
            _byOwner[ownerSerial] = owned;
        }

        owned.Add(key);
    }

    private void Forget(string ownerSerial, (Guid MapVersionId, Guid ResourceId) key)
    {
        if (!_byOwner.TryGetValue(ownerSerial, out var owned))
        {
            return;
        }

        var stillHeld = _byResource.TryGetValue(key, out var reservations)
            && reservations.Any(item => string.Equals(item.OwnerSerial, ownerSerial, StringComparison.Ordinal));

        if (!stillHeld)
        {
            owned.Remove(key);
        }

        if (owned.Count == 0)
        {
            _byOwner.Remove(ownerSerial);
        }
    }
}
