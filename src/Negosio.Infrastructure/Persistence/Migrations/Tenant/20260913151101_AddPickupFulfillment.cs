using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Negosio.Infrastructure.Persistence.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddPickupFulfillment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DeliveryReceipts_SaleId_SequenceNumber",
                table: "DeliveryReceipts");

            migrationBuilder.DropIndex(
                name: "IX_DeliveryReceipts_TenantId_Status_ScheduledDeliveryDate",
                table: "DeliveryReceipts");

            migrationBuilder.RenameColumn(
                name: "ScheduledDeliveryDate",
                table: "DeliveryReceipts",
                newName: "ScheduledDate");

            migrationBuilder.RenameColumn(
                name: "DeliveryNotes",
                table: "DeliveryReceipts",
                newName: "Notes");

            migrationBuilder.RenameColumn(
                name: "DeliveredByUserId",
                table: "DeliveryReceipts",
                newName: "CompletedByUserId");

            migrationBuilder.RenameColumn(
                name: "DeliveredAtUtc",
                table: "DeliveryReceipts",
                newName: "CompletedAtUtc");

            migrationBuilder.AlterColumn<string>(
                name: "DeliveryAddress",
                table: "DeliveryReceipts",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(300)",
                oldMaxLength: 300);

            migrationBuilder.AddColumn<int>(
                name: "CancellationDisposition",
                table: "DeliveryReceipts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Method",
                table: "DeliveryReceipts",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.CreateTable(
                name: "FulfillmentConversions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    FromMethod = table.Column<int>(type: "int", nullable: false),
                    ToMethod = table.Column<int>(type: "int", nullable: false),
                    SourceRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReplacementRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FulfillmentConversions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FulfillmentConversions_SaleItems_SaleItemId",
                        column: x => x.SaleItemId,
                        principalTable: "SaleItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FulfillmentConversions_Sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "Sales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryReceipts_SaleId_Method_SequenceNumber",
                table: "DeliveryReceipts",
                columns: new[] { "SaleId", "Method", "SequenceNumber" },
                unique: true,
                filter: "[SaleId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryReceipts_TenantId_Method_Status_ScheduledDate",
                table: "DeliveryReceipts",
                columns: new[] { "TenantId", "Method", "Status", "ScheduledDate" });

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentConversions_SaleId",
                table: "FulfillmentConversions",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentConversions_SaleItemId",
                table: "FulfillmentConversions",
                column: "SaleItemId");

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentConversions_SourceRecordId_SaleItemId",
                table: "FulfillmentConversions",
                columns: new[] { "SourceRecordId", "SaleItemId" },
                unique: true,
                filter: "[SourceRecordId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentConversions_TenantId_SaleId_CreatedAtUtc",
                table: "FulfillmentConversions",
                columns: new[] { "TenantId", "SaleId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FulfillmentConversions_TenantId_SaleItemId",
                table: "FulfillmentConversions",
                columns: new[] { "TenantId", "SaleItemId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FulfillmentConversions");

            migrationBuilder.DropIndex(
                name: "IX_DeliveryReceipts_SaleId_Method_SequenceNumber",
                table: "DeliveryReceipts");

            migrationBuilder.DropIndex(
                name: "IX_DeliveryReceipts_TenantId_Method_Status_ScheduledDate",
                table: "DeliveryReceipts");

            migrationBuilder.DropColumn(
                name: "CancellationDisposition",
                table: "DeliveryReceipts");

            migrationBuilder.DropColumn(
                name: "Method",
                table: "DeliveryReceipts");

            migrationBuilder.RenameColumn(
                name: "ScheduledDate",
                table: "DeliveryReceipts",
                newName: "ScheduledDeliveryDate");

            migrationBuilder.RenameColumn(
                name: "Notes",
                table: "DeliveryReceipts",
                newName: "DeliveryNotes");

            migrationBuilder.RenameColumn(
                name: "CompletedByUserId",
                table: "DeliveryReceipts",
                newName: "DeliveredByUserId");

            migrationBuilder.RenameColumn(
                name: "CompletedAtUtc",
                table: "DeliveryReceipts",
                newName: "DeliveredAtUtc");

            migrationBuilder.AlterColumn<string>(
                name: "DeliveryAddress",
                table: "DeliveryReceipts",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(300)",
                oldMaxLength: 300,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryReceipts_SaleId_SequenceNumber",
                table: "DeliveryReceipts",
                columns: new[] { "SaleId", "SequenceNumber" },
                unique: true,
                filter: "[SaleId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryReceipts_TenantId_Status_ScheduledDeliveryDate",
                table: "DeliveryReceipts",
                columns: new[] { "TenantId", "Status", "ScheduledDeliveryDate" });
        }
    }
}
