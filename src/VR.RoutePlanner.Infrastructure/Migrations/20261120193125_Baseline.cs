using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VR.RoutePlanner.Infrastructure.Migrations
{
    /// <summary>
    /// Baseline: фиксирует модель для последующих диффов и ничего не делает со схемой.
    /// Все таблицы <c>vrt_*</c> уже созданы миграциями <c>20261120000000_Stage1_Graph</c>
    /// и <c>20261120000100_Stage1_Rev4</c>, повторный <c>CreateTable</c> на существующей базе
    /// упал бы. Пустой <c>Up</c> добавляет только строку в историю миграций сервиса.
    /// </summary>
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Схема создана предыдущими миграциями: baseline фиксирует только модель.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Откат baseline ничего не удаляет: таблицы принадлежат предыдущим миграциям.
        }
    }
}
