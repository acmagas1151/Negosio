using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Negosio.Infrastructure.Persistence.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddRegisterCashMovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegisterCashMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegisterSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegisterCashMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegisterCashMovements_RegisterSessions_RegisterSessionId",
                        column: x => x.RegisterSessionId,
                        principalTable: "RegisterSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegisterCashMovements_RegisterSessionId",
                table: "RegisterCashMovements",
                column: "RegisterSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_RegisterCashMovements_TenantId_RegisterSessionId_CreatedAtUtc",
                table: "RegisterCashMovements",
                columns: new[] { "TenantId", "RegisterSessionId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegisterCashMovements");
        }
    }
}
