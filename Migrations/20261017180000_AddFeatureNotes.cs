using EduApi.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Hand-written (same pattern as AddPlanSwaps).
    ///
    /// FEATURE NOTES: text the SuperAdmin writes next to a feature so teachers can
    /// read it in full (e.g. students -> "up to 500 students").
    ///  - FeatureNotes table            : GLOBAL note per feature key
    ///  - FeaturePlans.FeatureNotesJson : per-PLAN notes ({ key: text }), win over the global one
    ///
    /// Requires 20261017120000_AddPlanSwaps.
    /// Matches Scripts/2026-10-17_add_feature_notes.sql exactly -- run ONE of the two
    /// against a given database, never both.
    /// </remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261017180000_AddFeatureNotes")]
    public partial class AddFeatureNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeatureNotes",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureNotes", x => x.Key);
                });

            migrationBuilder.AddColumn<string>(
                name: "FeatureNotesJson",
                table: "FeaturePlans",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "FeatureNotesJson", table: "FeaturePlans");
            migrationBuilder.DropTable(name: "FeatureNotes");
        }
    }
}
