using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Negosio.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class PerBranchDocumentNumberUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sales_TenantId_SaleNumber",
                table: "Sales");

            migrationBuilder.DropIndex(
                name: "IX_SaleReturns_TenantId_ReturnNumber",
                table: "SaleReturns");

            migrationBuilder.CreateIndex(
                name: "IX_Sales_TenantId_BranchId_SaleNumber",
                table: "Sales",
                columns: new[] { "TenantId", "BranchId", "SaleNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SaleReturns_TenantId_BranchId_ReturnNumber",
                table: "SaleReturns",
                columns: new[] { "TenantId", "BranchId", "ReturnNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sales_TenantId_BranchId_SaleNumber",
                table: "Sales");

            migrationBuilder.DropIndex(
                name: "IX_SaleReturns_TenantId_BranchId_ReturnNumber",
                table: "SaleReturns");

            migrationBuilder.CreateIndex(
                name: "IX_Sales_TenantId_SaleNumber",
                table: "Sales",
                columns: new[] { "TenantId", "SaleNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SaleReturns_TenantId_ReturnNumber",
                table: "SaleReturns",
                columns: new[] { "TenantId", "ReturnNumber" },
                unique: true);
        }
    }
}
