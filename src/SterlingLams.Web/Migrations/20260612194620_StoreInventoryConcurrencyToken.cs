using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <summary>
    /// Maps PostgreSQL's built-in <c>xmin</c> system column on "StoreInventories" as an EF optimistic
    /// concurrency token. <c>xmin</c> exists automatically on every Postgres table, so there is NO
    /// column to create — this migration is intentionally a no-op at the database level and exists
    /// only to record the model change in migration history / the snapshot.
    ///
    /// The auto-scaffolded version called <c>AddColumn("xmin")</c>, which fails on a real database with
    /// "column name \"xmin\" conflicts with a system column name". That has been removed.
    /// </summary>
    public partial class StoreInventoryConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No-op: "xmin" is a Postgres system column; the mapping is metadata-only (see class summary).
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: nothing was created, so nothing to drop.
        }
    }
}
