using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTrafficHits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrafficHits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    VisitorKey = table.Column<string>(type: "text", nullable: true),
                    RefererHost = table.Column<string>(type: "text", nullable: true),
                    Device = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrafficHits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrafficHits_CreatedAt",
                table: "TrafficHits",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TrafficHits_CreatedAt_VisitorKey",
                table: "TrafficHits",
                columns: new[] { "CreatedAt", "VisitorKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrafficHits");
        }
    }
}
