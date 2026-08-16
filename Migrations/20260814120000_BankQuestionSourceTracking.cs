using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Adds nullable SourceQuizId / SourceAssignmentId / SourceQuestionId to
    /// BankQuestions, so QuizzesController/AssignmentsController "export-to-bank"
    /// can tell which bank questions were auto-imported from a Quiz/Assignment
    /// question, and skip re-importing the same one on a second run.
    /// </remarks>
    public partial class BankQuestionSourceTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""BankQuestions""
                ADD COLUMN IF NOT EXISTS ""SourceQuizId"" integer NULL;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE ""BankQuestions""
                ADD COLUMN IF NOT EXISTS ""SourceAssignmentId"" integer NULL;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE ""BankQuestions""
                ADD COLUMN IF NOT EXISTS ""SourceQuestionId"" integer NULL;
            ");

            // Speeds up the "already exported?" existence check the export
            // endpoints run per question (SourceQuizId/SourceAssignmentId + SourceQuestionId).
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_BankQuestions_SourceQuizId_SourceQuestionId""
                ON ""BankQuestions"" (""SourceQuizId"", ""SourceQuestionId"");
            ");

            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_BankQuestions_SourceAssignmentId_SourceQuestionId""
                ON ""BankQuestions"" (""SourceAssignmentId"", ""SourceQuestionId"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_BankQuestions_SourceAssignmentId_SourceQuestionId"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_BankQuestions_SourceQuizId_SourceQuestionId"";");
            migrationBuilder.Sql(@"ALTER TABLE ""BankQuestions"" DROP COLUMN IF EXISTS ""SourceQuestionId"";");
            migrationBuilder.Sql(@"ALTER TABLE ""BankQuestions"" DROP COLUMN IF EXISTS ""SourceAssignmentId"";");
            migrationBuilder.Sql(@"ALTER TABLE ""BankQuestions"" DROP COLUMN IF EXISTS ""SourceQuizId"";");
        }
    }
}
