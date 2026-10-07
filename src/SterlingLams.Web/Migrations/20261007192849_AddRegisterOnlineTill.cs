using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddRegisterOnlineTill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HandlesOnlineOrders",
                table: "Registers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill: make each store's OLDEST register (lowest Id) its online till, so website sales
            // land on exactly one existing till from day one (the owner can move it afterwards). This is
            // what stops website revenue being counted on every open till at a store.
            migrationBuilder.Sql(@"
                UPDATE ""Registers"" SET ""HandlesOnlineOrders"" = true
                WHERE ""Id"" IN (SELECT MIN(""Id"") FROM ""Registers"" GROUP BY ""StoreId"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HandlesOnlineOrders",
                table: "Registers");
        }
    }
}
