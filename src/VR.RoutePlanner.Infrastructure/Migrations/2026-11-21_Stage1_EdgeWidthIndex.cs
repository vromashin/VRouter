using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace VR.RoutePlanner.Infrastructure.Migrations;

/// <summary>
/// Индекс <c>IX_vrt_EdgeWidth_EdgeId</c> есть в модели и в тексте <c>Rev4</c>, но в живую базу
/// не попал: <c>Rev4</c> записали в историю раньше, чем индекс появился в ее тексте.
/// Миграция досоздает индекс идемпотентно и ничего больше не меняет.
/// </summary>
[DbContext(typeof(Persistence.RoutePlannerDbContext))]
[Migration(MigrationId)]
public sealed class Stage1EdgeWidthIndex : Migration
{
    /// <summary>Идентификатор миграции.</summary>
    public const string MigrationId = "20261121120000_Stage1_EdgeWidthIndex";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(UpSql);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(DownSql);
    }

    private const string UpSql =
        """CREATE INDEX IF NOT EXISTS "IX_vrt_EdgeWidth_EdgeId" ON public."vrt_EdgeWidth" ("EdgeId");""";

    private const string DownSql = """DROP INDEX IF EXISTS public."IX_vrt_EdgeWidth_EdgeId";""";
}
