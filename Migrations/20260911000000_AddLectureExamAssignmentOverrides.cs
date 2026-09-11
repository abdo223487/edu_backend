using System;
using EduApi.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Same shape as AddStudentOverrides (20260828193706), just for
    /// LectureExams/LectureAssignments instead of Quizzes/Assignments. Hand
    /// written with no paired .Designer.cs, same as AddLectureViewLimits and
    /// AddLectureLinkFlags before it — see the remarks on
    /// AddLectureViewLimits for why that's fine here (both [Migration] and
    /// [DbContext] below are what EF actually needs to discover and apply
    /// this at Database.Migrate() time; BuildTargetModel is design-time-only
    /// and PendingModelChangesWarning is already suppressed in Program.cs).
    /// </remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260911000000_AddLectureExamAssignmentOverrides")]
    public partial class AddLectureExamAssignmentOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LectureExamStudentOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LectureExamId = table.Column<int>(type: "integer", nullable: false),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    ForceReview = table.Column<bool>(type: "boolean", nullable: false),
                    ReopenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LectureExamStudentOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LectureAssignmentStudentOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LectureAssignmentId = table.Column<int>(type: "integer", nullable: false),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    ForceReview = table.Column<bool>(type: "boolean", nullable: false),
                    ReopenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LectureAssignmentStudentOverrides", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LectureExamStudentOverrides_TeacherId",
                table: "LectureExamStudentOverrides",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_LectureExamStudentOverrides_LectureExamId_StudentId",
                table: "LectureExamStudentOverrides",
                columns: new[] { "LectureExamId", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LectureAssignmentStudentOverrides_TeacherId",
                table: "LectureAssignmentStudentOverrides",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_LectureAssignmentStudentOverrides_LectureAssignmentId_StudentId",
                table: "LectureAssignmentStudentOverrides",
                columns: new[] { "LectureAssignmentId", "StudentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LectureExamStudentOverrides");

            migrationBuilder.DropTable(
                name: "LectureAssignmentStudentOverrides");
        }
    }
}
