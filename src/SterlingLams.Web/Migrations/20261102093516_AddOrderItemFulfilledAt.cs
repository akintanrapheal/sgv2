using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderItemFulfilledAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FulfilledAt",
                table: "OrderItems",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill the per-item "handover" date for existing orders so EOD / cash-up / Completed
            // Transactions keep dating correctly the moment this ships. Postgres-only (prod); the SQLite
            // test harness builds the schema with EnsureCreated and never runs this. Two passes:
            //   1) a SPLIT order's SUPPLIER lines → the day that branch DISPATCHED its transfer, so an
            //      in-flight split order is credited on its real dispatch day (not the merge pack day);
            //   2) everything else (merge/pickup lines, single-branch orders) → the order's packed/ready
            //      time, exactly how those lines were dated before — so no historical report shifts.
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(@"
                    UPDATE ""OrderItems"" oi
                    SET ""FulfilledAt"" = st.""DispatchedAt""
                    FROM ""StockTransfers"" st
                    WHERE st.""OrderId"" = oi.""OrderId""
                      AND st.""FromStoreId"" = oi.""FulfilledFromStoreId""
                      AND st.""DispatchedAt"" IS NOT NULL
                      AND oi.""FulfilledAt"" IS NULL;");

                migrationBuilder.Sql(@"
                    UPDATE ""OrderItems"" oi
                    SET ""FulfilledAt"" = COALESCE(o.""PackedAt"", o.""PickupReadyEmailedAt"")
                    FROM ""Orders"" o
                    WHERE o.""Id"" = oi.""OrderId""
                      AND oi.""FulfilledAt"" IS NULL
                      AND COALESCE(o.""PackedAt"", o.""PickupReadyEmailedAt"") IS NOT NULL;");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FulfilledAt",
                table: "OrderItems");
        }
    }
}
