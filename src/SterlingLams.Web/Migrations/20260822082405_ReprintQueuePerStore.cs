using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class ReprintQueuePerStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The queue is transient (it regenerates on the next price change). Clear any existing
            // rows so the new NOT NULL StoreId + FK to Stores can't be violated by a StoreId=0 default.
            migrationBuilder.Sql("DELETE FROM \"LabelReprintQueue\";");

            migrationBuilder.DropIndex(
                name: "IX_LabelReprintQueue_ProductId",
                table: "LabelReprintQueue");

            migrationBuilder.AddColumn<int>(
                name: "StoreId",
                table: "LabelReprintQueue",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_LabelReprintQueue_ProductId_StoreId",
                table: "LabelReprintQueue",
                columns: new[] { "ProductId", "StoreId" },
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_LabelReprintQueue_StoreId",
                table: "LabelReprintQueue",
                column: "StoreId");

            migrationBuilder.AddForeignKey(
                name: "FK_LabelReprintQueue_Stores_StoreId",
                table: "LabelReprintQueue",
                column: "StoreId",
                principalTable: "Stores",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LabelReprintQueue_Stores_StoreId",
                table: "LabelReprintQueue");

            migrationBuilder.DropIndex(
                name: "IX_LabelReprintQueue_ProductId_StoreId",
                table: "LabelReprintQueue");

            migrationBuilder.DropIndex(
                name: "IX_LabelReprintQueue_StoreId",
                table: "LabelReprintQueue");

            migrationBuilder.DropColumn(
                name: "StoreId",
                table: "LabelReprintQueue");

            migrationBuilder.CreateIndex(
                name: "IX_LabelReprintQueue_ProductId",
                table: "LabelReprintQueue",
                column: "ProductId",
                unique: true,
                filter: "\"Status\" = 0");
        }
    }
}
