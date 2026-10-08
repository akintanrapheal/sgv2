using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreIsPublic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing branches stay customer-facing: backfill IsPublic = true for every current store
            // (and default new rows to true at the DB level too, matching the model). A branch is taken
            // off the website by unticking "Visible to customers", not by this migration.
            migrationBuilder.AddColumn<bool>(
                name: "IsPublic",
                table: "Stores",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPublic",
                table: "Stores");
        }
    }
}
