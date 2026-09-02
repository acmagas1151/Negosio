using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Negosio.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddRegisterSessionCashBreakdown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CashIn",
                table: "RegisterSessions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CashOut",
                table: "RegisterSessions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "GrossCashSales",
                table: "RegisterSessions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RefundCashOut",
                table: "RegisterSessions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VoidedCashSales",
                table: "RegisterSessions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CashIn",
                table: "RegisterSessions");

            migrationBuilder.DropColumn(
                name: "CashOut",
                table: "RegisterSessions");

            migrationBuilder.DropColumn(
                name: "GrossCashSales",
                table: "RegisterSessions");

            migrationBuilder.DropColumn(
                name: "RefundCashOut",
                table: "RegisterSessions");

            migrationBuilder.DropColumn(
                name: "VoidedCashSales",
                table: "RegisterSessions");
        }
    }
}
