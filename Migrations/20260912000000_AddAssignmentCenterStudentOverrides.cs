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
    /// Same shape as AddLectureExamAssignmentOverrides (20260911000000), just
    /// for AssignmentCenters instead of LectureExams/LectureAssignments —
    /// lets a teacher grant a specific student a force-review or reopen
    /// override on a "سنتر اسايمنت" (see AssignmentCenterStudentOverride).
    /// Hand written with no paired .Designer.cs, same as that migration and
    /// AddLectureViewLimits before it (see the remarks on AddLectureViewLimits
    /// for why that's fine here).
    /// </remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260912000000_AddAssignmentCenterStudentOverrides")]
    public partial class AddAssignmentCenterStudentOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssignmentCenterStudentOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssignmentCenterId = table.Column<int>(type: "integer", nullable: false),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    ForceReview = table.Column<bool>(type: "boolean", nullable: false),
                    ReopenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentCenterStudentOverrides", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentCenterStudentOverrides_TeacherId",
                table: "AssignmentCenterStudentOverrides",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentCenterStudentOverrides_AssignmentCenterId_StudentId",
                table: "AssignmentCenterStudentOverrides",
                columns: new[] { "AssignmentCenterId", "StudentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssignmentCenterStudentOverrides");
        }
    }
}
