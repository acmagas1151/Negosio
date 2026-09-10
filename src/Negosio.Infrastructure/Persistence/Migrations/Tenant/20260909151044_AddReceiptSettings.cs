using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Negosio.Infrastructure.Persistence.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddReceiptSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReceiptSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Width = table.Column<int>(type: "int", nullable: false),
                    SalesHeaderText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SalesFooterText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SalesShowBranch = table.Column<bool>(type: "bit", nullable: false),
                    SalesShowCashier = table.Column<bool>(type: "bit", nullable: false),
                    SalesShowPaymentMethod = table.Column<bool>(type: "bit", nullable: false),
                    SalesShowTaxLine = table.Column<bool>(type: "bit", nullable: false),
                    SalesShowReferenceNumber = table.Column<bool>(type: "bit", nullable: false),
                    DeliveryHeaderText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DeliveryFooterText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DeliveryShowPrices = table.Column<bool>(type: "bit", nullable: false),
                    DeliveryShowRelatedSaleNumber = table.Column<bool>(type: "bit", nullable: false),
                    DeliveryShowContactNumber = table.Column<bool>(type: "bit", nullable: false),
                    DeliveryShowSignatureFields = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiptSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_ReceiptSettings_TenantDefault",
                table: "ReceiptSettings",
                column: "TenantId",
                unique: true,
                filter: "[BranchId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_ReceiptSettings_TenantId_BranchId",
                table: "ReceiptSettings",
                columns: new[] { "TenantId", "BranchId" },
                unique: true,
                filter: "[BranchId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReceiptSettings");
        }
    }
}
