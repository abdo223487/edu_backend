using EduApi.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Hand-written (same pattern as AddSolutionSources): both [DbContext] and
    /// [Migration] are required for Database.Migrate() to pick this up.
    ///
    /// Teachers.DisabledFeatures: comma-separated dashboard feature keys the
    /// SuperAdmin switched off for a teacher (NULL = everything visible).
    ///
    /// Matches Scripts/2026-10-16_add_teacher_disabled_features.sql exactly --
    /// run ONE of the two against a given database, never both.
    /// </remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261016120000_AddTeacherDisabledFeatures")]
    public partial class AddTeacherDisabledFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisabledFeatures",
                table: "Teachers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisabledFeatures",
                table: "Teachers");
        }
    }
}
