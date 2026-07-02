using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class MarketingAutoCoupons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CouponEnabled",
                table: "Campaigns",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CouponExpiryDays",
                table: "Campaigns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "CouponMinOrder",
                table: "Campaigns",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CouponType",
                table: "Campaigns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "CouponValue",
                table: "Campaigns",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "CouponEnabled",
                table: "Automations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CouponExpiryDays",
                table: "Automations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "CouponMinOrder",
                table: "Automations",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CouponType",
                table: "Automations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "CouponValue",
                table: "Automations",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CouponEnabled",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "CouponExpiryDays",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "CouponMinOrder",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "CouponType",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "CouponValue",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "CouponEnabled",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "CouponExpiryDays",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "CouponMinOrder",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "CouponType",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "CouponValue",
                table: "Automations");
        }
    }
}
