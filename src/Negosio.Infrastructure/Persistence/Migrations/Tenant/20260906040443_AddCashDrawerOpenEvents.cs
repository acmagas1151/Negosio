using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Negosio.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddCashDrawerOpenEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CashDrawerOpenEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegisterSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashDrawerOpenEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashDrawerOpenEvents_RegisterSessions_RegisterSessionId",
                        column: x => x.RegisterSessionId,
                        principalTable: "RegisterSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CashDrawerOpenEvents_RegisterSessionId",
                table: "CashDrawerOpenEvents",
                column: "RegisterSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CashDrawerOpenEvents_TenantId_RegisterSessionId_CreatedAtUtc",
                table: "CashDrawerOpenEvents",
                columns: new[] { "TenantId", "RegisterSessionId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CashDrawerOpenEvents");
        }
    }
}
