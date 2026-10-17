using EduApi.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Hand-written (same pattern as AddFeaturePlansAndPreviewAccounts).
    ///
    /// PLAN SWAPS: a teacher may swap features 1-for-1 inside his plan, up to the plan's limit.
    ///  - FeaturePlans.SwapLimit : how many swaps the plan allows (0 = none)
    ///  - Teachers.SwapIn        : feature keys the teacher pulled INTO his plan (comma-separated)
    ///  - Teachers.SwapOut       : plan feature keys he gave up in exchange
    ///
    /// Requires 20261016180000_AddFeaturePlansAndPreviewAccounts.
    /// Matches Scripts/2026-10-17_add_plan_swaps.sql exactly -- run ONE of the two
    /// against a given database, never both.
    /// </remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261017120000_AddPlanSwaps")]
    public partial class AddPlanSwaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SwapLimit",
                table: "FeaturePlans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SwapIn",
                table: "Teachers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SwapOut",
                table: "Teachers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "SwapOut", table: "Teachers");
            migrationBuilder.DropColumn(name: "SwapIn", table: "Teachers");
            migrationBuilder.DropColumn(name: "SwapLimit", table: "FeaturePlans");
        }
    }
}
