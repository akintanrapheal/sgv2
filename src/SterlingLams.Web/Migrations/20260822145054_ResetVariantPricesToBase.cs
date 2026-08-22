using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class ResetVariantPricesToBase : Migration
    {
        // One-time normalisation: back up every variant's current price, then clear all overrides so
        // every variant follows its product's base price. Prices can then be adjusted per variant again.
        // Guarded to Postgres (the SQLite test harness builds its schema from the model, not migrations).
        private const string Backup = "\"VariantPriceBackup\"";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;

            // 1) Snapshot current variant prices so this is reversible (survives re-runs via IF NOT EXISTS).
            migrationBuilder.Sql($@"
CREATE TABLE IF NOT EXISTS {Backup} (
    ""VariantId"" integer PRIMARY KEY,
    ""OldPrice""  numeric(18,2) NOT NULL,
    ""BackedUpAt"" timestamptz NOT NULL DEFAULT now()
);
INSERT INTO {Backup} (""VariantId"", ""OldPrice"")
SELECT ""Id"", ""Price"" FROM ""ProductVariants"" WHERE ""Price"" IS NOT NULL
ON CONFLICT (""VariantId"") DO NOTHING;");

            // 2) Clear all overrides → every variant now follows the product base price.
            migrationBuilder.Sql(@"UPDATE ""ProductVariants"" SET ""Price"" = NULL WHERE ""Price"" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;

            // Restore the backed-up prices, then drop the backup table.
            migrationBuilder.Sql($@"
UPDATE ""ProductVariants"" v SET ""Price"" = b.""OldPrice""
FROM {Backup} b WHERE v.""Id"" = b.""VariantId"";
DROP TABLE IF EXISTS {Backup};");
        }
    }
}
