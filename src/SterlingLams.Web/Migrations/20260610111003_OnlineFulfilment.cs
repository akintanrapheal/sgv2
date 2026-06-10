using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class OnlineFulfilment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FulfillingStoreId",
                table: "Orders",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_FulfillingStoreId",
                table: "Orders",
                column: "FulfillingStoreId");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Stores_FulfillingStoreId",
                table: "Orders",
                column: "FulfillingStoreId",
                principalTable: "Stores",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Stores_FulfillingStoreId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_FulfillingStoreId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "FulfillingStoreId",
                table: "Orders");
        }
    }
}
