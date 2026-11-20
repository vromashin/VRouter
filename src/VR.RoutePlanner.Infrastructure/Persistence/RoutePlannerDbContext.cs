using Microsoft.EntityFrameworkCore;
using VR.RoutePlanner.Domain.Traffic;

namespace VR.RoutePlanner.Infrastructure.Persistence;

/// <summary>
/// Контекст базы <c>rms</c>. Свои таблицы (префикс <c>vrt_</c>) доступны на запись,
/// существующие таблицы склада (Maps, Nodes, Edges) только на чтение.
/// </summary>
public sealed class RoutePlannerDbContext : DbContext
{
    /// <summary>Создает контекст с заданными параметрами подключения.</summary>
    /// <param name="options">Параметры контекста.</param>
    public RoutePlannerDbContext(DbContextOptions<RoutePlannerDbContext> options)
        : base(options)
    {
    }

    /// <summary>Версии карт, по которым строился граф.</summary>
    public DbSet<MapVersionRecord> MapVersions => Set<MapVersionRecord>();

    /// <summary>Транспортные ресурсы.</summary>
    public DbSet<TrafficResourceRecord> TrafficResources => Set<TrafficResourceRecord>();

    /// <summary>Связи ресурсов с ребрами.</summary>
    public DbSet<TrafficResourceEdgeRecord> TrafficResourceEdges => Set<TrafficResourceEdgeRecord>();

    /// <summary>Связи ресурсов с узлами.</summary>
    public DbSet<TrafficResourceNodeRecord> TrafficResourceNodes => Set<TrafficResourceNodeRecord>();

    /// <summary>Конфликты ресурсов.</summary>
    public DbSet<ResourceConflictRecord> ResourceConflicts => Set<ResourceConflictRecord>();

    /// <summary>Ширины ребер по версиям карт.</summary>
    public DbSet<EdgeWidthRecord> EdgeWidths => Set<EdgeWidthRecord>();

    /// <summary>Проходимость ребер по профилям роботов.</summary>
    public DbSet<EdgePassabilityRecord> EdgePassabilities => Set<EdgePassabilityRecord>();

    /// <summary>Итог последнего прогрева по картам.</summary>
    public DbSet<WarmupStateRecord> WarmupStates => Set<WarmupStateRecord>();

    /// <summary>Строки карт склада (только чтение).</summary>
    public DbSet<RmsMapRecord> Maps => Set<RmsMapRecord>();

    /// <summary>Строки узлов склада (только чтение).</summary>
    public DbSet<RmsNodeRecord> Nodes => Set<RmsNodeRecord>();

    /// <summary>Строки ребер склада (только чтение).</summary>
    public DbSet<RmsEdgeRecord> Edges => Set<RmsEdgeRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<MapVersionRecord>(builder =>
        {
            builder.ToTable("vrt_MapVersion", "public");
            builder.HasKey(record => record.Id);
            builder.Property(record => record.MapId).HasMaxLength(128).IsRequired();
            builder.Property(record => record.MapVersion).HasMaxLength(128).IsRequired();
            builder.Property(record => record.GraphVersion).HasMaxLength(128).IsRequired();
            builder.Property(record => record.CreatedAt).HasColumnName("CreatedAt");
            builder.HasIndex(record => new { record.MapId, record.GraphVersion }).IsUnique().HasDatabaseName("UQ_vrt_MapVersion_MapId_GraphVersion");
        });

        modelBuilder.Entity<TrafficResourceRecord>(builder =>
        {
            builder.ToTable("vrt_TrafficResource", "public");
            builder.HasKey(record => record.Id);
            builder.Property(record => record.MapVersionId).HasColumnName("MapVersionId");
            builder.Property(record => record.Code).HasMaxLength(256).IsRequired();
            builder.Property(record => record.AgvProfile).HasMaxLength(128).IsRequired();
            builder.Property(record => record.ResourceType).HasMaxLength(32).HasConversion<string>().IsRequired();
            builder.HasIndex(record => new { record.MapVersionId, record.AgvProfile, record.Code }).IsUnique().HasDatabaseName("UQ_vrt_TrafficResource_MapVersion_Profile_Code");
        });

        modelBuilder.Entity<TrafficResourceEdgeRecord>(builder =>
        {
            builder.ToTable("vrt_TrafficResourceEdge", "public");
            builder.HasKey(record => new { record.TrafficResourceId, record.EdgeId, record.Direction });
            builder.Property(record => record.Direction).HasMaxLength(16).IsRequired();
            builder.Property(record => record.AgvProfile).HasMaxLength(128).IsRequired();
            builder.HasIndex(record => new { record.EdgeId, record.Direction, record.AgvProfile }).IsUnique().HasDatabaseName("UQ_vrt_TrafficResourceEdge_Edge_Direction_Profile");
        });

        modelBuilder.Entity<EdgePassabilityRecord>(builder =>
        {
            builder.ToTable("vrt_EdgePassability", "public");
            builder.HasKey(record => new { record.MapVersionId, record.AgvProfile, record.EdgeId });
            builder.Property(record => record.AgvProfile).HasMaxLength(128).IsRequired();
            builder.Property(record => record.CorridorClass).HasColumnName("CorridorClass").HasMaxLength(8).IsRequired();
            builder.Property(record => record.Reason).HasMaxLength(64);
            builder.HasIndex(record => new { record.EdgeId, record.AgvProfile });
        });

        modelBuilder.Entity<TrafficResourceNodeRecord>(builder =>
        {
            builder.ToTable("vrt_TrafficResourceNode", "public");
            builder.HasKey(record => new { record.TrafficResourceId, record.NodeId });
            builder.Property(record => record.AgvProfile).HasMaxLength(128).IsRequired();
            builder.HasIndex(record => new { record.NodeId, record.AgvProfile }).IsUnique().HasDatabaseName("UQ_vrt_TrafficResourceNode_Node_Profile");
        });

        modelBuilder.Entity<ResourceConflictRecord>(builder =>
        {
            builder.ToTable("vrt_ResourceConflict", "public");
            builder.HasKey(record => new { record.ResourceId, record.ConflictingResourceId });
            builder.Property(record => record.ConflictType).HasMaxLength(64).IsRequired();
            builder.HasIndex(record => record.ResourceId);
        });

        modelBuilder.Entity<EdgeWidthRecord>(builder =>
        {
            builder.ToTable("vrt_EdgeWidth", "public");
            builder.HasKey(record => new { record.MapVersionId, record.EdgeId });
            builder.Property(record => record.WidthSource).HasMaxLength(16).IsRequired();
            builder.Property(record => record.WidthSourceDetail).HasMaxLength(256).IsRequired();
            builder.HasIndex(record => record.EdgeId);
        });

        modelBuilder.Entity<WarmupStateRecord>(builder =>
        {
            builder.ToTable("vrt_WarmupState", "public");
            builder.HasKey(record => record.MapId);
            builder.Property(record => record.MapId).HasMaxLength(128).IsRequired();
            builder.Property(record => record.GraphVersion).HasMaxLength(128).IsRequired();
            builder.Property(record => record.WidthSource).HasMaxLength(16).IsRequired();
            builder.Property(record => record.ResourceCount).IsRequired();
            builder.Property(record => record.ImpassableCount).IsRequired();
        });

        modelBuilder.Entity<RmsMapRecord>(builder =>
        {
            builder.ToTable("Maps", "public", table => table.ExcludeFromMigrations());
            builder.HasKey(record => record.Id);
        });

        modelBuilder.Entity<RmsNodeRecord>(builder =>
        {
            builder.ToTable("Nodes", "public", table => table.ExcludeFromMigrations());
            builder.HasKey(record => record.Id);
        });

        modelBuilder.Entity<RmsEdgeRecord>(builder =>
        {
            builder.ToTable("Edges", "public", table => table.ExcludeFromMigrations());
            builder.HasKey(record => record.Id);
            builder.Property(record => record.Trajectory).HasColumnType("jsonb");
        });
    }
}

/// <summary>Строка таблицы <c>public."Maps"</c>.</summary>
public sealed class RmsMapRecord
{
    /// <summary>Идентификатор строки.</summary>
    public Guid Id { get; set; }

    /// <summary>Идентификатор карты, например 801.</summary>
    public string MapId { get; set; } = string.Empty;

    /// <summary>Версия карты.</summary>
    public string MapVersion { get; set; } = string.Empty;

    /// <summary>Имя объекта.</summary>
    public string? FacilityName { get; set; }

    /// <summary>Ширина карты, метры.</summary>
    public double Width { get; set; }

    /// <summary>Высота карты, метры.</summary>
    public double Height { get; set; }

    /// <summary>Смещение начала координат по X, метры.</summary>
    public double OriginX { get; set; }

    /// <summary>Смещение начала координат по Y, метры.</summary>
    public double OriginY { get; set; }

    /// <summary>Разрешение растра, метры на пиксель.</summary>
    public double Resolution { get; set; }

    /// <summary>Ссылка на изображение карты.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Ссылка на скачивание карты.</summary>
    public string? MapDownloadLink { get; set; }

    /// <summary>Признак активности карты.</summary>
    public bool IsActive { get; set; }
}

/// <summary>Строка таблицы <c>public."Nodes"</c>.</summary>
public sealed class RmsNodeRecord
{
    /// <summary>Идентификатор строки.</summary>
    public Guid Id { get; set; }

    /// <summary>Идентификатор узла в карте.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Идентификатор карты.</summary>
    public Guid MapId { get; set; }

    /// <summary>Координата X, метры.</summary>
    public double PositionX { get; set; }

    /// <summary>Координата Y, метры.</summary>
    public double PositionY { get; set; }

    /// <summary>Угол, радианы.</summary>
    public double Theta { get; set; }

    /// <summary>Допустимое отклонение по XY, метры.</summary>
    public double? AllowedDeviationXY { get; set; }

    /// <summary>Допустимое отклонение по углу, радианы.</summary>
    public double? AllowedDeviationTheta { get; set; }

    /// <summary>Тип узла.</summary>
    public string? NodeType { get; set; }

    /// <summary>Идентификатор стеллажа.</summary>
    public Guid? RackId { get; set; }

    /// <summary>Высота узла, метры.</summary>
    public double? Z { get; set; }

    /// <summary>Признак активности узла.</summary>
    public bool IsActive { get; set; }
}

/// <summary>Строка таблицы <c>public."Edges"</c>.</summary>
public sealed class RmsEdgeRecord
{
    /// <summary>Идентификатор строки.</summary>
    public Guid Id { get; set; }

    /// <summary>Идентификатор ребра в карте.</summary>
    public string EdgeId { get; set; } = string.Empty;

    /// <summary>Идентификатор начального узла.</summary>
    public Guid FromNodeId { get; set; }

    /// <summary>Идентификатор конечного узла.</summary>
    public Guid ToNodeId { get; set; }

    /// <summary>Идентификатор карты.</summary>
    public Guid MapId { get; set; }

    /// <summary>Объявленная длина ребра.</summary>
    public double Length { get; set; }

    /// <summary>Максимальная скорость, метры в секунду.</summary>
    public double? MaxSpeed { get; set; }

    /// <summary>Признак двустороннего движения.</summary>
    public bool Bidirectional { get; set; }

    /// <summary>Коридор, к которому относится ребро.</summary>
    public string? Corridor { get; set; }

    /// <summary>Максимальная скорость вращения.</summary>
    public double? MaxRotationSpeed { get; set; }

    /// <summary>Ориентация ребра.</summary>
    public double? Orientation { get; set; }

    /// <summary>Тип ориентации.</summary>
    public string? OrientationType { get; set; }

    /// <summary>Разрешено ли вращение.</summary>
    public bool? RotationAllowed { get; set; }

    /// <summary>Траектория ребра в формате NURBS (jsonb).</summary>
    public string? Trajectory { get; set; }

    /// <summary>Признак активности ребра.</summary>
    public bool IsActive { get; set; }
}

/// <summary>Строка таблицы <c>public."vrt_MapVersion"</c>.</summary>
public sealed class MapVersionRecord
{
    /// <summary>Идентификатор записи.</summary>
    public Guid Id { get; set; }

    /// <summary>Идентификатор карты.</summary>
    public string MapId { get; set; } = string.Empty;

    /// <summary>Версия карты.</summary>
    public string MapVersion { get; set; } = string.Empty;

    /// <summary>Версия графа с хэшем состава.</summary>
    public string GraphVersion { get; set; } = string.Empty;

    /// <summary>Момент создания записи, UTC.</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>Строка таблицы <c>public."vrt_TrafficResource"</c>.</summary>
public sealed class TrafficResourceRecord
{
    /// <summary>Идентификатор ресурса.</summary>
    public Guid Id { get; set; }

    /// <summary>Идентификатор версии карты.</summary>
    public Guid MapVersionId { get; set; }

    /// <summary>Код ресурса.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Профиль робота.</summary>
    public string AgvProfile { get; set; } = string.Empty;

    /// <summary>Тип ресурса.</summary>
    public TrafficResourceType ResourceType { get; set; }

    /// <summary>Емкость ресурса.</summary>
    public int Capacity { get; set; }

    /// <summary>Минимальный интервал входа, секунды.</summary>
    public double MinEntryInterval { get; set; }

    /// <summary>Максимальное время занятия, секунды.</summary>
    public double MaxOccupancyTime { get; set; }

    /// <summary>Признак активности ресурса.</summary>
    public bool IsActive { get; set; }
}

/// <summary>Строка таблицы <c>public."vrt_TrafficResourceEdge"</c>.</summary>
public sealed class TrafficResourceEdgeRecord
{
    /// <summary>Идентификатор ресурса.</summary>
    public Guid TrafficResourceId { get; set; }

    /// <summary>Идентификатор ребра.</summary>
    public Guid EdgeId { get; set; }

    /// <summary>Направление обхода.</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>Порядок ребра внутри ресурса.</summary>
    public int Sequence { get; set; }

    /// <summary>Профиль робота.</summary>
    public string AgvProfile { get; set; } = string.Empty;
}

/// <summary>Строка таблицы <c>public."vrt_EdgePassability"</c>.</summary>
public sealed class EdgePassabilityRecord
{
    /// <summary>Идентификатор версии карты.</summary>
    public Guid MapVersionId { get; set; }

    /// <summary>Идентификатор ребра.</summary>
    public Guid EdgeId { get; set; }

    /// <summary>Профиль робота.</summary>
    public string AgvProfile { get; set; } = string.Empty;

    /// <summary>Признак проходимости ребра для профиля.</summary>
    public bool IsPassable { get; set; }

    /// <summary>Класс коридора, например <c>C3</c>.</summary>
    public string CorridorClass { get; set; } = string.Empty;

    /// <summary>Причина непроходимости.</summary>
    public string? Reason { get; set; }
}

/// <summary>Строка таблицы <c>public."vrt_TrafficResourceNode"</c>.</summary>
public sealed class TrafficResourceNodeRecord
{
    /// <summary>Идентификатор ресурса.</summary>
    public Guid TrafficResourceId { get; set; }

    /// <summary>Идентификатор узла.</summary>
    public Guid NodeId { get; set; }

    /// <summary>Профиль робота.</summary>
    public string AgvProfile { get; set; } = string.Empty;
}

/// <summary>Строка таблицы <c>public."vrt_EdgeWidth"</c>.</summary>
public sealed class EdgeWidthRecord
{
    /// <summary>Идентификатор версии карты.</summary>
    public Guid MapVersionId { get; set; }

    /// <summary>Идентификатор ребра.</summary>
    public Guid EdgeId { get; set; }

    /// <summary>Ширина коридора, метры.</summary>
    public double Width { get; set; }

    /// <summary>Источник значения ширины.</summary>
    public string WidthSource { get; set; } = string.Empty;

    /// <summary>Уточнение источника.</summary>
    public string? WidthSourceDetail { get; set; }
}

/// <summary>Строка таблицы <c>public."vrt_ResourceConflict"</c>.</summary>
public sealed class ResourceConflictRecord
{
    /// <summary>Идентификатор первого ресурса.</summary>
    public Guid ResourceId { get; set; }

    /// <summary>Идентификатор второго ресурса.</summary>
    public Guid ConflictingResourceId { get; set; }

    /// <summary>Тип конфликта.</summary>
    public string ConflictType { get; set; } = string.Empty;
}

/// <summary>
/// Строка таблицы <c>public."vrt_WarmupState"</c>: последний завершенный прогрев карты,
/// общий для Api и Workers.
/// </summary>
public sealed class WarmupStateRecord
{
    /// <summary>Идентификатор карты, одна строка на карту.</summary>
    public string MapId { get; set; } = string.Empty;

    /// <summary>Идентификатор версии карты.</summary>
    public Guid MapVersionId { get; set; }

    /// <summary>Версия графа, для которой считались ресурсы.</summary>
    public string GraphVersion { get; set; } = string.Empty;

    /// <summary>Число активных ребер карты.</summary>
    public int EdgeCount { get; set; }

    /// <summary>Источник ширин коридоров.</summary>
    public string WidthSource { get; set; } = string.Empty;

    /// <summary>Число ресурсов по профилям роботов, JSON объект.</summary>
    public string ResourceCount { get; set; } = "{}";

    /// <summary>Число непроходимых ребер по профилям роботов, JSON объект.</summary>
    public string ImpassableCount { get; set; } = "{}";

    /// <summary>Длительность прогрева в миллисекундах.</summary>
    public long DurationMs { get; set; }

    /// <summary>Ширины взяты из <c>vrt_EdgeWidth</c> без пересчета по растру.</summary>
    public bool ReusedWidths { get; set; }

    /// <summary>Момент завершения прогрева, UTC.</summary>
    public DateTimeOffset CompletedAt { get; set; }
}
