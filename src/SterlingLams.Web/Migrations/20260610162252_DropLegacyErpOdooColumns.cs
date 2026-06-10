using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyErpOdooColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Stores_ErpNextWarehouse",
                table: "Stores");

            migrationBuilder.DropIndex(
                name: "IX_Products_ErpNextItemCode",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ErpNextWarehouse",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "ErpNextInvoiceName",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OdooCategoryId",
                table: "Categories");

            migrationBuilder.RenameColumn(
                name: "ErpNextItemCode",
                table: "Products",
                newName: "ExternalCode");

            migrationBuilder.CreateIndex(
                name: "IX_Products_ExternalCode",
                table: "Products",
                column: "ExternalCode",
                unique: true,
                filter: "\"ExternalCode\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_ExternalCode",
                table: "Products");

            migrationBuilder.RenameColumn(
                name: "ExternalCode",
                table: "Products",
                newName: "ErpNextItemCode");

            migrationBuilder.AddColumn<string>(
                name: "ErpNextWarehouse",
                table: "Stores",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ErpNextInvoiceName",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OdooCategoryId",
                table: "Categories",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Stores_ErpNextWarehouse",
                table: "Stores",
                column: "ErpNextWarehouse",
                unique: true,
                filter: "\"ErpNextWarehouse\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Products_ErpNextItemCode",
                table: "Products",
                column: "ErpNextItemCode",
                unique: true,
                filter: "\"ErpNextItemCode\" <> ''");
        }
    }
}
