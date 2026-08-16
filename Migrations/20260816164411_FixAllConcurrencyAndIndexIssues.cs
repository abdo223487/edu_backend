using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    public partial class FixAllConcurrencyAndIndexIssues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BankQuestions_SourceAssignmentId_SourceQuestionId",
                table: "BankQuestions");

            migrationBuilder.DropIndex(
                name: "IX_BankQuestions_SourceQuizId_SourceQuestionId",
                table: "BankQuestions");

            migrationBuilder.CreateIndex(
                name: "IX_QuizResults_QuizId_StudentId",
                table: "QuizResults",
                columns: new[] { "QuizId", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Codes_SourceCodeTemplateId_UsedByStudentId",
                table: "Codes",
                columns: new[] { "SourceCodeTemplateId", "UsedByStudentId" },
                unique: true,
                filter: "\"SourceCodeTemplateId\" IS NOT NULL AND \"UsedByStudentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Codes_Value",
                table: "Codes",
                column: "Value",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attendances_LectureId_StudentId",
                table: "Attendances",
                columns: new[] { "LectureId", "StudentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QuizResults_QuizId_StudentId",
                table: "QuizResults");

            migrationBuilder.DropIndex(
                name: "IX_Codes_SourceCodeTemplateId_UsedByStudentId",
                table: "Codes");

            migrationBuilder.DropIndex(
                name: "IX_Codes_Value",
                table: "Codes");

            migrationBuilder.DropIndex(
                name: "IX_Attendances_LectureId_StudentId",
                table: "Attendances");

            migrationBuilder.CreateIndex(
                name: "IX_BankQuestions_SourceAssignmentId_SourceQuestionId",
                table: "BankQuestions",
                columns: new[] { "SourceAssignmentId", "SourceQuestionId" });

            migrationBuilder.CreateIndex(
                name: "IX_BankQuestions_SourceQuizId_SourceQuestionId",
                table: "BankQuestions",
                columns: new[] { "SourceQuizId", "SourceQuestionId" });
        }
    }
}
