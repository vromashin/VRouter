using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace VR.RoutePlanner.Infrastructure.Migrations;

/// <summary>
/// Ревизия 4: проходимость ребер перенесена из связи ресурса в отдельную таблицу
/// <c>vrt_EdgePassability</c>, добавлена служебная таблица прогрева
/// <c>vrt_WarmupState</c> (спека этапа 1, часть 4). Скрипт идемпотентный.
/// </summary>
[DbContext(typeof(Persistence.RoutePlannerDbContext))]
[Migration(MigrationId)]
public sealed class Stage1Rev4 : Migration
{
    /// <summary>Идентификатор миграции.</summary>
    public const string MigrationId = "20261120000100_Stage1_Rev4";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var statement in UpStatements)
        {
            migrationBuilder.Sql(statement);
        }
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var statement in DownStatements)
        {
            migrationBuilder.Sql(statement);
        }
    }

    private static readonly string[] UpStatements =
    {
        """
        CREATE TABLE IF NOT EXISTS public."vrt_EdgePassability" (
            "MapVersionId" uuid NOT NULL REFERENCES public."vrt_MapVersion"("Id"),
            "EdgeId" uuid NOT NULL REFERENCES public."Edges"("Id"),
            "AgvProfile" varchar(128) NOT NULL,
            "IsPassable" boolean NOT NULL,
            "CorridorClass" varchar(8) NOT NULL,
            "Reason" varchar(64),
            PRIMARY KEY ("MapVersionId", "AgvProfile", "EdgeId")
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS "IX_vrt_EdgePassability_Edge_Profile"
            ON public."vrt_EdgePassability" ("EdgeId", "AgvProfile");
        """,
        """ALTER TABLE public."vrt_TrafficResourceEdge" DROP COLUMN IF EXISTS "Class";""",
        """ALTER TABLE public."vrt_TrafficResourceEdge" DROP COLUMN IF EXISTS "IsPassable";""",
        """
        CREATE INDEX IF NOT EXISTS "IX_vrt_EdgeWidth_EdgeId"
            ON public."vrt_EdgeWidth" ("EdgeId");
        """,
        """
        CREATE TABLE IF NOT EXISTS public."vrt_WarmupState" (
            "MapId" varchar(128) NOT NULL,
            "MapVersionId" uuid NOT NULL,
            "GraphVersion" varchar(128) NOT NULL,
            "EdgeCount" integer NOT NULL,
            "WidthSource" varchar(16) NOT NULL,
            "ResourceCount" text NOT NULL,
            "ImpassableCount" text NOT NULL,
            "DurationMs" bigint NOT NULL,
            "ReusedWidths" boolean NOT NULL,
            "CompletedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_vrt_WarmupState" PRIMARY KEY ("MapId")
        );
        """,
    };

    private static readonly string[] DownStatements =
    {
        """DROP TABLE IF EXISTS public."vrt_WarmupState";""",
        """DROP TABLE IF EXISTS public."vrt_EdgePassability";""",
    };
}
