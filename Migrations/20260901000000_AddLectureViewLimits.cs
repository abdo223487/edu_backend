using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// BUGFIX: same class of bug as AddExternalBooks and
    /// AddStudentGroupMemberships before it -- EF Core discovers migrations
    /// at runtime by scanning the assembly for classes carrying
    /// [Migration("id")] (normally emitted into a paired .Designer.cs file
    /// that `dotnet ef migrations add` generates). This file was
    /// hand-written without one, so Database.Migrate() never even saw it as
    /// a candidate, let alone ran it -- the app would start "successfully"
    /// but every request touching ViewLimit/StudentLectureViewUsages would
    /// crash with a Postgres "column/relation does not exist" error the
    /// first time it ran. The [Migration] attribute below is the fix -- a
    /// full Designer.cs (with BuildTargetModel) is only needed for
    /// design-time model-diffing, which this project already doesn't rely
    /// on for hand-written migrations like this one (see
    /// PendingModelChangesWarning being suppressed in Program.cs).
    /// </remarks>
    [Migration("20260901000000_AddLectureViewLimits")]
    public partial class AddLectureViewLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ViewLimit",
                table: "Lectures",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StudentLectureViewUsages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    LectureId = table.Column<int>(type: "integer", nullable: false),
                    ViewsUsed = table.Column<int>(type: "integer", nullable: false),
                    ExtraViews = table.Column<int>(type: "integer", nullable: false),
                    LastViewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentLectureViewUsages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StudentLectureViewUsages_TeacherId",
                table: "StudentLectureViewUsages",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentLectureViewUsages_StudentId_LectureId",
                table: "StudentLectureViewUsages",
                columns: new[] { "StudentId", "LectureId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StudentLectureViewUsages");

            migrationBuilder.DropColumn(
                name: "ViewLimit",
                table: "Lectures");
        }
    }
}
