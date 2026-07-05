using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class AbandonedCartReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RemindersSent",
                table: "AbandonedCarts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing carts that already got the old single recovery email are at step 1, so the new
            // sequence continues from step 2 instead of re-sending the first email.
            migrationBuilder.Sql(@"UPDATE ""AbandonedCarts"" SET ""RemindersSent"" = 1 WHERE ""EmailedAt"" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RemindersSent",
                table: "AbandonedCarts");
        }
    }
}
