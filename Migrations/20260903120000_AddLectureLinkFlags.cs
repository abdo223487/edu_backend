using EduApi.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Same hand-written pattern as AddLectureViewLimits: both [DbContext]
    /// and [Migration] attributes are required for EF's MigrationsAssembly
    /// scan to pick this up at Database.Migrate() time -- no paired
    /// Designer.cs needed (see the longer note on AddLectureViewLimits).
    /// </remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260903120000_AddLectureLinkFlags")]
    public partial class AddLectureLinkFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireLinkExam",
                table: "Lectures",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireLinkAssignment",
                table: "Lectures",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequireLinkExam",
                table: "Lectures");

            migrationBuilder.DropColumn(
                name: "RequireLinkAssignment",
                table: "Lectures");
        }
    }
}
