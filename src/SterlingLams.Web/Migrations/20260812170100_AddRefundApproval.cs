using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovedByUserId",
                table: "Refunds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DecisionAt",
                table: "Refunds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionNote",
                table: "Refunds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RestockRequested",
                table: "Refunds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RestockStoreId",
                table: "Refunds",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "Refunds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "WasFullRefund",
                table: "Refunds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Every refund that already exists genuinely happened (money + stock already moved), so mark
            // them Approved — otherwise historical refunds would show as "pending approval" and drop out
            // of the finance figures.
            migrationBuilder.Sql(@"UPDATE ""Refunds"" SET ""Status"" = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedByUserId",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "DecisionAt",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "DecisionNote",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "RestockRequested",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "RestockStoreId",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "WasFullRefund",
                table: "Refunds");
        }
    }
}
