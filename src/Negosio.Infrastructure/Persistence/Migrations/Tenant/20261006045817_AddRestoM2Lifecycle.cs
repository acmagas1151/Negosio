using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Negosio.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddRestoM2Lifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CancelApprovedByUserId",
                table: "RestoOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PricesIncludeTaxSnapshot",
                table: "RestoOrders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SettlementRequestId",
                table: "RestoOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxRatePercentSnapshot",
                table: "RestoOrders",
                type: "decimal(9,4)",
                precision: 9,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "UnpaidClosedAtUtc",
                table: "RestoOrders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UnpaidClosedByUserId",
                table: "RestoOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UnpaidClosureApprovedByUserId",
                table: "RestoOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnpaidClosureReason",
                table: "RestoOrders",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UnpaidClosureRequestId",
                table: "RestoOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CostPriceSnapshot",
                table: "RestoOrderItems",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DiscountApprovedByUserId",
                table: "RestoOrderItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscountKind",
                table: "RestoOrderItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountValue",
                table: "RestoOrderItems",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "SaleItemModifiers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModifierGroupNameSnapshot = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ModifierOptionNameSnapshot = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PriceDeltaSnapshot = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleItemModifiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SaleItemModifiers_SaleItems_SaleItemId",
                        column: x => x.SaleItemId,
                        principalTable: "SaleItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RestoOrders_TenantId_SettlementRequestId",
                table: "RestoOrders",
                columns: new[] { "TenantId", "SettlementRequestId" },
                unique: true,
                filter: "[SettlementRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RestoOrders_TenantId_UnpaidClosureRequestId",
                table: "RestoOrders",
                columns: new[] { "TenantId", "UnpaidClosureRequestId" },
                unique: true,
                filter: "[UnpaidClosureRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SaleItemModifiers_SaleItemId",
                table: "SaleItemModifiers",
                column: "SaleItemId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleItemModifiers_TenantId_SaleItemId",
                table: "SaleItemModifiers",
                columns: new[] { "TenantId", "SaleItemId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SaleItemModifiers");

            migrationBuilder.DropIndex(
                name: "IX_RestoOrders_TenantId_SettlementRequestId",
                table: "RestoOrders");

            migrationBuilder.DropIndex(
                name: "IX_RestoOrders_TenantId_UnpaidClosureRequestId",
                table: "RestoOrders");

            migrationBuilder.DropColumn(
                name: "CancelApprovedByUserId",
                table: "RestoOrders");

            migrationBuilder.DropColumn(
                name: "PricesIncludeTaxSnapshot",
                table: "RestoOrders");

            migrationBuilder.DropColumn(
                name: "SettlementRequestId",
                table: "RestoOrders");

            migrationBuilder.DropColumn(
                name: "TaxRatePercentSnapshot",
                table: "RestoOrders");

            migrationBuilder.DropColumn(
                name: "UnpaidClosedAtUtc",
                table: "RestoOrders");

            migrationBuilder.DropColumn(
                name: "UnpaidClosedByUserId",
                table: "RestoOrders");

            migrationBuilder.DropColumn(
                name: "UnpaidClosureApprovedByUserId",
                table: "RestoOrders");

            migrationBuilder.DropColumn(
                name: "UnpaidClosureReason",
                table: "RestoOrders");

            migrationBuilder.DropColumn(
                name: "UnpaidClosureRequestId",
                table: "RestoOrders");

            migrationBuilder.DropColumn(
                name: "CostPriceSnapshot",
                table: "RestoOrderItems");

            migrationBuilder.DropColumn(
                name: "DiscountApprovedByUserId",
                table: "RestoOrderItems");

            migrationBuilder.DropColumn(
                name: "DiscountKind",
                table: "RestoOrderItems");

            migrationBuilder.DropColumn(
                name: "DiscountValue",
                table: "RestoOrderItems");
        }
    }
}
