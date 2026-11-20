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
            "MapVersionId" uuid NOT NULL REFERENCES public."vrt_MapVersion"("Id"),
            "AgvProfile" varchar(128) NOT NULL,
            "Code" varchar(256) NOT NULL,
            "ResourceType" varchar(32) NOT NULL,
            "Capacity" integer NOT NULL,
            "MinEntryInterval" double precision NOT NULL DEFAULT 0,
            "MaxOccupancyTime" double precision NOT NULL DEFAULT 0,
            "IsActive" boolean NOT NULL DEFAULT true,
            CONSTRAINT "UQ_vrt_TrafficResource_Id_Profile" UNIQUE ("Id", "AgvProfile"),
            CONSTRAINT "UQ_vrt_TrafficResource_MapVersion_Profile_Code"
                UNIQUE ("MapVersionId", "AgvProfile", "Code")
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS public."vrt_TrafficResourceEdge" (
            "TrafficResourceId" uuid NOT NULL,
            "AgvProfile" varchar(128) NOT NULL,
            "EdgeId" uuid NOT NULL REFERENCES public."Edges"("Id"),
            "Direction" varchar(16) NOT NULL,
            "Sequence" integer NOT NULL,
            PRIMARY KEY ("TrafficResourceId", "EdgeId", "Direction"),
            CONSTRAINT "FK_vrt_TrafficResourceEdge_Resource"
                FOREIGN KEY ("TrafficResourceId", "AgvProfile")
                REFERENCES public."vrt_TrafficResource" ("Id", "AgvProfile"),
            CONSTRAINT "UQ_vrt_TrafficResourceEdge_Edge_Direction_Profile"
                UNIQUE ("EdgeId", "Direction", "AgvProfile")
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS public."vrt_TrafficResourceNode" (
            "TrafficResourceId" uuid NOT NULL,
            "AgvProfile" varchar(128) NOT NULL,
            "NodeId" uuid NOT NULL REFERENCES public."Nodes"("Id"),
            PRIMARY KEY ("TrafficResourceId", "NodeId"),
            CONSTRAINT "FK_vrt_TrafficResourceNode_Resource"
                FOREIGN KEY ("TrafficResourceId", "AgvProfile")
                REFERENCES public."vrt_TrafficResource" ("Id", "AgvProfile"),
            CONSTRAINT "UQ_vrt_TrafficResourceNode_Node_Profile"
                UNIQUE ("NodeId", "AgvProfile")
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
        CREATE TABLE IF NOT EXISTS public."vrt_EdgeWidth" (
            "MapVersionId" uuid NOT NULL REFERENCES public."vrt_MapVersion"("Id"),
            "EdgeId" uuid NOT NULL REFERENCES public."Edges"("Id"),
            "Width" double precision NOT NULL,
            "WidthSource" varchar(16) NOT NULL,
            "WidthSourceDetail" varchar(256) NOT NULL,
            PRIMARY KEY ("MapVersionId", "EdgeId")
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS "IX_vrt_ResourceConflict_ResourceId"
            ON public."vrt_ResourceConflict" ("ResourceId");
        """,
    };

    private static readonly string[] DropStatements =
    {
        """DROP TABLE IF EXISTS public."vrt_ResourceConflict";""",
        """DROP TABLE IF EXISTS public."vrt_EdgeWidth";""",
        """DROP TABLE IF EXISTS public."vrt_TrafficResourceNode";""",
        """DROP TABLE IF EXISTS public."vrt_TrafficResourceEdge";""",
        """DROP TABLE IF EXISTS public."vrt_TrafficResource";""",
        """DROP TABLE IF EXISTS public."vrt_MapVersion";""",
    };
}
