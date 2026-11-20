using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace VR.RoutePlanner.Infrastructure.Migrations;

/// <summary>
/// Первая миграция сервиса: только собственные таблицы <c>vrt_</c> в схеме <c>public</c>.
/// Таблицы <c>Maps</c>, <c>Nodes</c>, <c>Edges</c> сервис читает и никогда не создает и не меняет.
/// </summary>
[DbContext(typeof(Persistence.RoutePlannerDbContext))]
[Migration(MigrationId)]
public sealed class Stage1Graph : Migration
{
    /// <summary>Идентификатор миграции.</summary>
    public const string MigrationId = "20261120000000_Stage1_Graph";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var statement in CreateStatements)
        {
            migrationBuilder.Sql(statement);
        }
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var statement in DropStatements)
        {
            migrationBuilder.Sql(statement);
        }
    }

    private static readonly string[] CreateStatements =
    {
        """
        CREATE TABLE IF NOT EXISTS public."vrt_MapVersion" (
            "Id" uuid PRIMARY KEY,
            "MapId" varchar(128) NOT NULL,
            "MapVersion" varchar(128) NOT NULL,
            "GraphVersion" varchar(128) NOT NULL,
            "CreatedAt" timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT "UQ_vrt_MapVersion_MapId_GraphVersion" UNIQUE ("MapId", "GraphVersion")
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS public."vrt_TrafficResource" (
            "Id" uuid PRIMARY KEY,
            "Code" varchar(256) NOT NULL,
            "ResourceType" varchar(32) NOT NULL,
            "Capacity" integer NOT NULL,
            "MinEntryInterval" double precision NOT NULL DEFAULT 0,
            "MaxOccupancyTime" double precision NOT NULL DEFAULT 0,
            "IsActive" boolean NOT NULL DEFAULT true,
            CONSTRAINT "UQ_vrt_TrafficResource_Code" UNIQUE ("Code")
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS public."vrt_TrafficResourceEdge" (
            "TrafficResourceId" uuid NOT NULL,
            "EdgeId" uuid NOT NULL,
            "Direction" varchar(16) NOT NULL,
            "Sequence" integer NOT NULL,
            PRIMARY KEY ("TrafficResourceId", "EdgeId", "Direction"),
            CONSTRAINT "FK_vrt_TrafficResourceEdge_TrafficResource"
                FOREIGN KEY ("TrafficResourceId") REFERENCES public."vrt_TrafficResource" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_vrt_TrafficResourceEdge_Edge"
                FOREIGN KEY ("EdgeId") REFERENCES public."Edges" ("Id"),
            CONSTRAINT "UQ_vrt_TrafficResourceEdge_Edge_Direction" UNIQUE ("EdgeId", "Direction")
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS public."vrt_TrafficResourceNode" (
            "TrafficResourceId" uuid NOT NULL,
            "NodeId" uuid NOT NULL,
            PRIMARY KEY ("TrafficResourceId", "NodeId"),
            CONSTRAINT "FK_vrt_TrafficResourceNode_TrafficResource"
                FOREIGN KEY ("TrafficResourceId") REFERENCES public."vrt_TrafficResource" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_vrt_TrafficResourceNode_Node"
                FOREIGN KEY ("NodeId") REFERENCES public."Nodes" ("Id")
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS public."vrt_ResourceConflict" (
            "ResourceId" uuid NOT NULL,
            "ConflictingResourceId" uuid NOT NULL,
            "ConflictType" varchar(64) NOT NULL,
            PRIMARY KEY ("ResourceId", "ConflictingResourceId"),
            CONSTRAINT "FK_vrt_ResourceConflict_Resource"
                FOREIGN KEY ("ResourceId") REFERENCES public."vrt_TrafficResource" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_vrt_ResourceConflict_ConflictingResource"
                FOREIGN KEY ("ConflictingResourceId") REFERENCES public."vrt_TrafficResource" ("Id") ON DELETE CASCADE
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS "IX_vrt_TrafficResourceEdge_EdgeId"
            ON public."vrt_TrafficResourceEdge" ("EdgeId");
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_vrt_TrafficResourceNode_NodeId"
            ON public."vrt_TrafficResourceNode" ("NodeId");
        """,
        """
        CREATE INDEX IF NOT EXISTS "IX_vrt_ResourceConflict_ResourceId"
            ON public."vrt_ResourceConflict" ("ResourceId");
        """,
    };

    private static readonly string[] DropStatements =
    {
        """DROP TABLE IF EXISTS public."vrt_ResourceConflict";""",
        """DROP TABLE IF EXISTS public."vrt_TrafficResourceNode";""",
        """DROP TABLE IF EXISTS public."vrt_TrafficResourceEdge";""",
        """DROP TABLE IF EXISTS public."vrt_TrafficResource";""",
        """DROP TABLE IF EXISTS public."vrt_MapVersion";""",
    };
}
