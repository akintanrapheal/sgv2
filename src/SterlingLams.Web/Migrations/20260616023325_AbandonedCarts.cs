using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class AbandonedCarts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AbandonedCarts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Token = table.Column<string>(type: "text", nullable: false),
                    ItemsJson = table.Column<string>(type: "text", nullable: false),
                    Subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EmailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RecoveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AbandonedCarts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AbandonedCarts_Email",
                table: "AbandonedCarts",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AbandonedCarts_RecoveredAt_EmailedAt_CreatedAt",
                table: "AbandonedCarts",
                columns: new[] { "RecoveredAt", "EmailedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AbandonedCarts_Token",
                table: "AbandonedCarts",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AbandonedCarts");
        }
    }
}
