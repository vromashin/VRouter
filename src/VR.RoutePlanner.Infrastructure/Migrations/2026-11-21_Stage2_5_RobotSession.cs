using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace VR.RoutePlanner.Infrastructure.Migrations;

/// <summary>
/// Этап 2.5: таблицы сессии робота, заказа и узлов заказа. Скрипт идемпотентный:
/// таблицы создаются с <c>IF NOT EXISTS</c>, индекс и внешний ключ — защищенно.
/// </summary>
[DbContext(typeof(Persistence.RoutePlannerDbContext))]
[Migration(MigrationId)]
public sealed class Stage2_5_RobotSession : Migration
{
    /// <summary>Идентификатор миграции.</summary>
    public const string MigrationId = "20261121130000_Stage2_5_RobotSession";

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
        CREATE TABLE IF NOT EXISTS public."vrt_RobotSession" (
            "AgvSerialNumber" varchar(64) NOT NULL,
            "Manufacturer" varchar(64),
            "ProtocolVersion" varchar(16),
            "ConnectionState" varchar(32),
            "LastSeenAt" timestamp with time zone NOT NULL,
            "StateJson" text,
            "UpdatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_vrt_RobotSession" PRIMARY KEY ("AgvSerialNumber")
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS public."vrt_RobotOrder" (
            "OrderId" varchar(32) NOT NULL,
            "AgvSerialNumber" varchar(64) NOT NULL,
            "OrderUpdateId" integer NOT NULL,
            "Status" varchar(32) NOT NULL,
            "PlanJson" text,
            "ReleasedNodeCount" integer NOT NULL,
            "TargetNodeId" varchar(64),
            "ApproachNodeId" varchar(64),
            "CreatedAt" timestamp with time zone NOT NULL,
            "SentAt" timestamp with time zone,
            "AcceptedAt" timestamp with time zone,
            "CompletedAt" timestamp with time zone,
            "FailureReason" varchar(256),
            CONSTRAINT "PK_vrt_RobotOrder" PRIMARY KEY ("OrderId")
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS "IX_vrt_RobotOrder_AgvSerialNumber"
            ON public."vrt_RobotOrder" ("AgvSerialNumber");
        """,
        """
        CREATE TABLE IF NOT EXISTS public."vrt_RobotOrderNode" (
            "OrderId" varchar(32) NOT NULL,
            "SequenceId" integer NOT NULL,
            "NodeId" varchar(64) NOT NULL,
            "Released" boolean NOT NULL,
            "Reached" boolean NOT NULL,
            "ActionCount" integer NOT NULL,
            CONSTRAINT "PK_vrt_RobotOrderNode" PRIMARY KEY ("OrderId", "SequenceId"),
            CONSTRAINT "FK_vrt_RobotOrderNode_vrt_RobotOrder_OrderId"
                FOREIGN KEY ("OrderId") REFERENCES public."vrt_RobotOrder" ("OrderId") ON DELETE CASCADE
        );
        """,
    };

    private static readonly string[] DownStatements =
    {
        """DROP TABLE IF EXISTS public."vrt_RobotOrderNode";""",
        """DROP TABLE IF EXISTS public."vrt_RobotOrder";""",
        """DROP TABLE IF EXISTS public."vrt_RobotSession";""",
    };
}
