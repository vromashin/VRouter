using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VR.RoutePlanner.Infrastructure.Migrations
{
    /// <summary>
    /// Таблица <c>public."vrt_WarmupState"</c>: итог последнего прогрева карты. Api и Workers
    /// это разные процессы, поэтому результат хранится в базе, а не в памяти процесса.
    /// Скрипт идемпотентный.
    /// </summary>
    public partial class WarmupState : Migration
    {
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
            """;

        private const string DownSql =
            """DROP TABLE IF EXISTS public."vrt_WarmupState";""";
    }
}
