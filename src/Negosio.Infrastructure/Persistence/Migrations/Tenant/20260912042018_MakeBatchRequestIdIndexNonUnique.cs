using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Negosio.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class MakeBatchRequestIdIndexNonUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DeliveryReceipts_TenantId_BatchRequestId",
                table: "DeliveryReceipts");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryReceipts_TenantId_BatchRequestId",
                table: "DeliveryReceipts",
                columns: new[] { "TenantId", "BatchRequestId" },
                filter: "[BatchRequestId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DeliveryReceipts_TenantId_BatchRequestId",
                table: "DeliveryReceipts");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryReceipts_TenantId_BatchRequestId",
                table: "DeliveryReceipts",
                columns: new[] { "TenantId", "BatchRequestId" },
                unique: true,
                filter: "[BatchRequestId] IS NOT NULL");
        }
    }
}
