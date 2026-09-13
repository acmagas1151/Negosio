using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Negosio.Infrastructure.Persistence.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddDeliveryFulfillment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This migration is deliberately restructured from what EF scaffolded, into three phases:
            //   Phase 1 — add every new column, with the two columns that need a per-row historical
            //             value (DeliveryReceipts.ScheduledDeliveryDate, DeliveryReceiptItems.SaleItemId)
            //             temporarily NULLABLE, and with every index that touches them deferred.
            //   Phase 2 — backfill those columns for pre-existing (legacy) rows from the best available
            //             historical data.
            //   Phase 3 — tighten both columns to NOT NULL, then add the deferred indexes and the FK.
            // EF's own scaffold made both columns NOT NULL with fabricated sentinel defaults
            // (0001-01-01 and the all-zeros Guid). Those would have silently written meaningless values
            // into real rows — and the all-zeros Guid would additionally have violated the new
            // DeliveryReceiptItems -> SaleItems foreign key. The Phase 3 ordering below is self-verifying:
            // if Phase 2 leaves any row unmatched, the ALTER to NOT NULL fails loudly instead of
            // persisting a fabricated value.

            // ---------------------------------------------------------------------------------------
            // Phase 1 — schema, with the two backfill-needed columns temporarily nullable
            // ---------------------------------------------------------------------------------------

            // Superseded by the wider indexes created below (SaleId -> SaleId+SequenceNumber, and
            // DeliveryReceiptId -> DeliveryReceiptId+SaleItemId, the latter added in Phase 3).
            migrationBuilder.DropIndex(
                name: "IX_DeliveryReceipts_SaleId",
                table: "DeliveryReceipts");

            migrationBuilder.DropIndex(
                name: "IX_DeliveryReceiptItems_DeliveryReceiptId",
                table: "DeliveryReceiptItems");

            // Every pre-existing SaleItem correctly defaults to fully take-now (nothing delivery-required);
            // the rows that were actually on a legacy delivery receipt are corrected in Phase 2.
            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryRequiredQuantity",
                table: "SaleItems",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "BatchRequestId",
                table: "DeliveryReceipts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                table: "DeliveryReceipts",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAtUtc",
                table: "DeliveryReceipts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CancelledByUserId",
                table: "DeliveryReceipts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveredAtUtc",
                table: "DeliveryReceipts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeliveredByUserId",
                table: "DeliveryReceipts",
                type: "uniqueidentifier",
                nullable: true);

            // SQL Server populates rowversion on every existing row automatically — no backfill needed.
            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "DeliveryReceipts",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            // TEMPORARILY NULLABLE (EF scaffolded this NOT NULL with a fabricated 0001-01-01 default).
            // No sensible static default exists; every legacy row is given a real per-row value in
            // Phase 2, and the column is tightened to NOT NULL in Phase 3.
            migrationBuilder.AddColumn<DateOnly>(
                name: "ScheduledDeliveryDate",
                table: "DeliveryReceipts",
                type: "date",
                nullable: true);

            // Legacy deliveries were always the sale's one and only delivery, so 1 is correct for them.
            migrationBuilder.AddColumn<int>(
                name: "SequenceNumber",
                table: "DeliveryReceipts",
                type: "int",
                nullable: false,
                defaultValue: 1);

            // 3 == FulfillmentStatus.Completed. Legacy delivery receipts recorded a completed handover —
            // the pre-plan feature had no pending/cancelled concept — so Completed is their correct status.
            // Originally this migration set defaultValue: 2 (the old DeliveryStatus.Delivered numeric value),
            // but Task 1 renamed the enum to use (Unscheduled=1, Pending=2, Completed=3, Cancelled=4), so we
            // update the default to 3 to preserve the semantics (legacy rows are Completed, not Pending).
            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "DeliveryReceipts",
                type: "int",
                nullable: false,
                defaultValue: 3);

            // TEMPORARILY NULLABLE (EF scaffolded this NOT NULL with an all-zeros Guid default, which
            // would have violated the new FK added in Phase 3). Backfilled per row in Phase 2.
            migrationBuilder.AddColumn<Guid>(
                name: "SaleItemId",
                table: "DeliveryReceiptItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryReceipts_SaleId_SequenceNumber",
                table: "DeliveryReceipts",
                columns: new[] { "SaleId", "SequenceNumber" },
                unique: true,
                filter: "[SaleId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryReceipts_TenantId_BatchRequestId",
                table: "DeliveryReceipts",
                columns: new[] { "TenantId", "BatchRequestId" },
                unique: true,
                filter: "[BatchRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryReceipts_TenantId_BranchId_CreatedAtUtc",
                table: "DeliveryReceipts",
                columns: new[] { "TenantId", "BranchId", "CreatedAtUtc" });

            // ---------------------------------------------------------------------------------------
            // Phase 2 — legacy-data backfill
            // ---------------------------------------------------------------------------------------

            // Legacy DeliveryReceipts were always whole-sale, one row per SaleItem, created by
            // ResolveItems(sale, request) with `request.Items is null` OR an explicit but exhaustive
            // list — either way, each DeliveryReceiptItem's (ProductNameSnapshot, VariantNameSnapshot)
            // pair matches exactly one SaleItem on that DeliveryReceipt's Sale, because checkout already
            // merges duplicate-variant lines into one SaleItem per variant per sale. Both sides are
            // immutable point-in-time snapshots from the same sale event, so this match is exact.
            // Self-verification for the multi-match case. The zero-match case already fails loudly at
            // Phase 3's ALTER to NOT NULL, but a row matching TWO sale items would not: T-SQL's
            // UPDATE...FROM...JOIN resolves an ambiguous match by picking one arbitrarily and silently,
            // linking to *a* valid row rather than necessarily the *right* one (and leaving the true
            // match's SaleItem at DeliveryRequiredQuantity = 0). Verified 0 such rows across every
            // local tenant database, but a future database must fail loudly rather than guess.
            // RETURN after RAISERROR because severity 16 does not itself abort the batch — without it
            // the non-deterministic UPDATE below would still run before the error surfaced.
            migrationBuilder.Sql(@"
                IF EXISTS (
                    SELECT 1
                    FROM DeliveryReceiptItems dri
                    JOIN DeliveryReceipts dr ON dr.Id = dri.DeliveryReceiptId
                    JOIN SaleItems si ON si.SaleId = dr.SaleId
                        AND si.ProductNameSnapshot = dri.ProductNameSnapshot
                        AND ISNULL(si.VariantNameSnapshot, N'') = ISNULL(dri.VariantNameSnapshot, N'')
                    WHERE dri.SaleItemId IS NULL
                    GROUP BY dri.Id
                    HAVING COUNT(*) > 1
                )
                BEGIN
                    RAISERROR('AddDeliveryFulfillment migration: one or more DeliveryReceiptItem rows matched more than one SaleItem by (SaleId, ProductNameSnapshot, VariantNameSnapshot) — the backfill cannot safely proceed. Investigate the ambiguous row(s) before re-running.', 16, 1);
                    RETURN;
                END

                UPDATE dri
                SET dri.SaleItemId = si.Id
                FROM DeliveryReceiptItems dri
                JOIN DeliveryReceipts dr ON dr.Id = dri.DeliveryReceiptId
                JOIN SaleItems si ON si.SaleId = dr.SaleId
                    AND si.ProductNameSnapshot = dri.ProductNameSnapshot
                    AND ISNULL(si.VariantNameSnapshot, N'') = ISNULL(dri.VariantNameSnapshot, N'')
                WHERE dri.SaleItemId IS NULL;
            ");

            // Best available historical value: the DR's own creation date. Documented assumption, not
            // a precise "when the truck actually arrived" timestamp — none was ever captured pre-plan.
            migrationBuilder.Sql(@"
                UPDATE DeliveryReceipts
                SET ScheduledDeliveryDate = CAST(CreatedAtUtc AS date)
                WHERE ScheduledDeliveryDate IS NULL;
            ");

            // Best available historical actor/time for "delivered" — DeliveredAtUtc/DeliveredByUserId
            // stay nullable (only meaningful once Completed), but every legacy row defaulted Status to
            // Completed in Phase 1 (defaultValue: 3 after Task 1 enum rename), so populate them
            // consistently rather than leaving a Completed row with null delivered-audit fields.
            // Documented assumption: PreparedByUserId (the only actor ever recorded before this plan)
            // stands in for "delivered by," and CreatedAtUtc stands in for "delivered at" — no better data exists.
            migrationBuilder.Sql(@"
                UPDATE DeliveryReceipts
                SET DeliveredAtUtc = CreatedAtUtc, DeliveredByUserId = PreparedByUserId
                WHERE Status = 3 AND DeliveredAtUtc IS NULL;
            ");

            // Every SaleItem on a sale that never had a delivery receipt correctly keeps
            // DeliveryRequiredQuantity = 0 from Phase 1's defaultValue — no further backfill needed;
            // those items stay entirely Take-now, which is the existing (pre-plan) behavior unchanged.
            // For sale items that WERE on a legacy (now-Delivered) delivery receipt, their full sold
            // quantity was delivery-required (the old feature had no concept of partial/take-now split):
            migrationBuilder.Sql(@"
                UPDATE si
                SET si.DeliveryRequiredQuantity = si.Quantity
                FROM SaleItems si
                WHERE EXISTS (
                    SELECT 1 FROM DeliveryReceiptItems dri WHERE dri.SaleItemId = si.Id
                );
            ");

            // ---------------------------------------------------------------------------------------
            // Phase 3 — tighten to NOT NULL, then the deferred indexes and the FK
            // ---------------------------------------------------------------------------------------
            // These two ALTERs are the safety net: if Phase 2 failed to match any row, they fail here
            // rather than leaving a fabricated value behind. If that happens, do not force it —
            // investigate the unmatched row.

            migrationBuilder.AlterColumn<DateOnly>(
                name: "ScheduledDeliveryDate",
                table: "DeliveryReceipts",
                type: "date",
                nullable: false,
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SaleItemId",
                table: "DeliveryReceiptItems",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryReceipts_TenantId_Status_ScheduledDeliveryDate",
                table: "DeliveryReceipts",
                columns: new[] { "TenantId", "Status", "ScheduledDeliveryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryReceiptItems_DeliveryReceiptId_SaleItemId",
                table: "DeliveryReceiptItems",
                columns: new[] { "DeliveryReceiptId", "SaleItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryReceiptItems_SaleItemId",
                table: "DeliveryReceiptItems",
                column: "SaleItemId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryReceiptItems_TenantId_SaleItemId",
                table: "DeliveryReceiptItems",
                columns: new[] { "TenantId", "SaleItemId" });

            // Added last, after the backfill, so SQL Server validates every backfilled SaleItemId
            // against a real SaleItem row as part of applying this migration.
            migrationBuilder.AddForeignKey(
                name: "FK_DeliveryReceiptItems_SaleItems_SaleItemId",
                table: "DeliveryReceiptItems",
                column: "SaleItemId",
                principalTable: "SaleItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Exact reverse of Up(): undo Phase 3, then Phase 1. Phase 2's backfill needs no explicit
            // reversal — every column it wrote is dropped below, except SaleItems.DeliveryRequiredQuantity,
            // which is likewise dropped.

            // --- reverse Phase 3 ---
            migrationBuilder.DropForeignKey(
                name: "FK_DeliveryReceiptItems_SaleItems_SaleItemId",
                table: "DeliveryReceiptItems");

            migrationBuilder.DropIndex(
                name: "IX_DeliveryReceiptItems_TenantId_SaleItemId",
                table: "DeliveryReceiptItems");

            migrationBuilder.DropIndex(
                name: "IX_DeliveryReceiptItems_SaleItemId",
                table: "DeliveryReceiptItems");

            migrationBuilder.DropIndex(
                name: "IX_DeliveryReceiptItems_DeliveryReceiptId_SaleItemId",
                table: "DeliveryReceiptItems");

            migrationBuilder.DropIndex(
                name: "IX_DeliveryReceipts_TenantId_Status_ScheduledDeliveryDate",
                table: "DeliveryReceipts");

            // Indexes must be gone before the columns they cover can be altered back to nullable.
            migrationBuilder.AlterColumn<Guid>(
                name: "SaleItemId",
                table: "DeliveryReceiptItems",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: false);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "ScheduledDeliveryDate",
                table: "DeliveryReceipts",
                type: "date",
                nullable: true,
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: false);

            // --- reverse Phase 1 ---
            migrationBuilder.DropIndex(
                name: "IX_DeliveryReceipts_TenantId_BranchId_CreatedAtUtc",
                table: "DeliveryReceipts");

            migrationBuilder.DropIndex(
                name: "IX_DeliveryReceipts_TenantId_BatchRequestId",
                table: "DeliveryReceipts");

            migrationBuilder.DropIndex(
                name: "IX_DeliveryReceipts_SaleId_SequenceNumber",
                table: "DeliveryReceipts");

            migrationBuilder.DropColumn(
                name: "SaleItemId",
                table: "DeliveryReceiptItems");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "DeliveryReceipts");

            migrationBuilder.DropColumn(
                name: "SequenceNumber",
                table: "DeliveryReceipts");

            migrationBuilder.DropColumn(
                name: "ScheduledDeliveryDate",
                table: "DeliveryReceipts");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "DeliveryReceipts");

            migrationBuilder.DropColumn(
                name: "DeliveredByUserId",
                table: "DeliveryReceipts");

            migrationBuilder.DropColumn(
                name: "DeliveredAtUtc",
                table: "DeliveryReceipts");

            migrationBuilder.DropColumn(
                name: "CancelledByUserId",
                table: "DeliveryReceipts");

            migrationBuilder.DropColumn(
                name: "CancelledAtUtc",
                table: "DeliveryReceipts");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "DeliveryReceipts");

            migrationBuilder.DropColumn(
                name: "BatchRequestId",
                table: "DeliveryReceipts");

            migrationBuilder.DropColumn(
                name: "DeliveryRequiredQuantity",
                table: "SaleItems");

            // Restore the two indexes dropped at the top of Up().
            migrationBuilder.CreateIndex(
                name: "IX_DeliveryReceiptItems_DeliveryReceiptId",
                table: "DeliveryReceiptItems",
                column: "DeliveryReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryReceipts_SaleId",
                table: "DeliveryReceipts",
                column: "SaleId");
        }
    }
}
