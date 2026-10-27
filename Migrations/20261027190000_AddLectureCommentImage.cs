using EduApi.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Hand-written (same pattern as AddStudentEngagement) -- do NOT run
    /// "dotnet ef migrations add": this project's model snapshot is intentionally out of sync.
    ///
    /// LECTURE Q&amp;A IMAGES: adds LectureComments.ImageUrl (optional image attached to a
    /// question or reply). Idempotent, so it is also safe if
    /// Scripts/2026-10-27_lecture_comment_image.sql was already run by hand.
    /// </remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261027190000_AddLectureCommentImage")]
    public partial class AddLectureCommentImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE ""LectureComments"" ADD COLUMN IF NOT EXISTS ""ImageUrl"" character varying(500) NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""LectureComments"" DROP COLUMN IF EXISTS ""ImageUrl"";");
        }
    }
}
