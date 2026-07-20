using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Lets a teacher attach a bank question directly to a Unit, without
    /// picking a Lesson first (BankQuestionsController.Create). Makes
    /// LessonId nullable and adds a nullable UnitId — exactly one of the
    /// two is set per question.
    /// </remarks>
    public partial class BankQuestionDirectToUnit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""BankQuestions""
                ALTER COLUMN ""LessonId"" DROP NOT NULL;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE ""BankQuestions""
                ADD COLUMN IF NOT EXISTS ""UnitId"" integer NULL;
            ");

            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_BankQuestions_UnitId"" ON ""BankQuestions"" (""UnitId"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_BankQuestions_UnitId"";
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE ""BankQuestions""
                DROP COLUMN IF EXISTS ""UnitId"";
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE ""BankQuestions""
                ALTER COLUMN ""LessonId"" SET NOT NULL;
            ");
        }
    }
}
