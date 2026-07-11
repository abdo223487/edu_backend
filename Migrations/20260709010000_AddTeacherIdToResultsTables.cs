using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// MULTI-TENANT SECURITY FIX: QuizResult, CenterQuizResult and HomeworkResult
    /// were only ever filtered by StudentId, so a student subscribed to more than
    /// one teacher had marks from ALL of their teachers mixed together no matter
    /// which tenant asked. This adds TeacherId to each table and backfills it:
    ///   - QuizResults: copied from the parent Quiz (exact, since every online
    ///     quiz result already belongs to exactly one Quiz -> one tenant).
    ///   - CenterQuizResults / HomeworkResults: these are manually-entered,
    ///     standalone rows with no parent to copy from, so existing rows are
    ///     backfilled from the student's current (legacy) group's teacher --
    ///     correct for every pre-existing row, since multi-tenant membership
    ///     only shipped in AddStudentGroupMemberships just before this.
    /// Applied automatically via Database.Migrate() at startup, same as every
    /// other real migration in this project -- no manual SQL step needed.
    /// </remarks>
    public partial class AddTeacherIdToResultsTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TeacherId",
                table: "QuizResults",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TeacherId",
                table: "CenterQuizResults",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TeacherId",
                table: "HomeworkResults",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill QuizResults from the parent Quiz's TeacherId.
            migrationBuilder.Sql(
                @"UPDATE ""QuizResults"" qr
                  SET ""TeacherId"" = q.""TeacherId""
                  FROM ""Quizzes"" q
                  WHERE q.""Id"" = qr.""QuizId"";");

            // Backfill CenterQuizResults / HomeworkResults from the student's
            // current group's TeacherId (best available signal for pre-existing
            // rows -- see remarks above).
            migrationBuilder.Sql(
                @"UPDATE ""CenterQuizResults"" cr
                  SET ""TeacherId"" = g.""TeacherId""
                  FROM ""Students"" s
                  JOIN ""Groups"" g ON g.""Id"" = s.""GroupId""
                  WHERE s.""Id"" = cr.""StudentId"";");

            migrationBuilder.Sql(
                @"UPDATE ""HomeworkResults"" hr
                  SET ""TeacherId"" = g.""TeacherId""
                  FROM ""Students"" s
                  JOIN ""Groups"" g ON g.""Id"" = s.""GroupId""
                  WHERE s.""Id"" = hr.""StudentId"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TeacherId",
                table: "QuizResults");

            migrationBuilder.DropColumn(
                name: "TeacherId",
                table: "CenterQuizResults");

            migrationBuilder.DropColumn(
                name: "TeacherId",
                table: "HomeworkResults");
        }
    }
}
