using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class TransferWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Quantity",
                table: "StockTransferItems",
                newName: "RequestedQty");

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                table: "StockTransfers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedByUserId",
                table: "StockTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                table: "StockTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "StockTransfers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledByUserId",
                table: "StockTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CourierName",
                table: "StockTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DispatchNotes",
                table: "StockTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DispatchedAt",
                table: "StockTransfers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DispatchedByUserId",
                table: "StockTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiveNotes",
                table: "StockTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReceivedAt",
                table: "StockTransfers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceivedByUserId",
                table: "StockTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RejectedAt",
                table: "StockTransfers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectedByUserId",
                table: "StockTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "StockTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "StockTransfers",
                type: "integer",
                nullable: false,
                defaultValue: 5); // existing (instant-transfer) rows become Completed

            migrationBuilder.AddColumn<string>(
                name: "TrackingNumber",
                table: "StockTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovedQty",
                table: "StockTransferItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DispatchedQty",
                table: "StockTransferItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReceivedQty",
                table: "StockTransferItems",
                type: "integer",
                nullable: true);

            // Legacy (pre-workflow) transfers were executed instantly — backfill their
            // approval/dispatch/receive quantities and timestamps so the new timeline
            // and items table show them as fully completed.
            migrationBuilder.Sql(
                @"UPDATE ""StockTransferItems"" SET ""ApprovedQty"" = ""RequestedQty"", ""DispatchedQty"" = ""RequestedQty"", ""ReceivedQty"" = ""RequestedQty"";");
            migrationBuilder.Sql(
                @"UPDATE ""StockTransfers"" SET ""ApprovedAt"" = ""CreatedAt"", ""DispatchedAt"" = ""CreatedAt"", ""ReceivedAt"" = ""CreatedAt"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "ApprovedByUserId",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "CancelledByUserId",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "CourierName",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "DispatchNotes",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "DispatchedAt",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "DispatchedByUserId",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "ReceiveNotes",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "ReceivedAt",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "ReceivedByUserId",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "RejectedAt",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "RejectedByUserId",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "TrackingNumber",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "ApprovedQty",
                table: "StockTransferItems");

            migrationBuilder.DropColumn(
                name: "DispatchedQty",
                table: "StockTransferItems");

            migrationBuilder.DropColumn(
                name: "ReceivedQty",
                table: "StockTransferItems");

            migrationBuilder.RenameColumn(
                name: "RequestedQty",
                table: "StockTransferItems",
                newName: "Quantity");
        }
    }
}
