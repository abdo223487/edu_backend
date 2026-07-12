using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// PERFORMANCE: every table below has a global query filter
    /// (WHERE "TeacherId" = @tenant) applied to literally every query EF
    /// generates against it -- see AppDbContext.OnModelCreating. Without an
    /// index, Postgres has no way to satisfy that filter other than a
    /// sequential scan of the entire table on every request. That's invisible
    /// with a handful of demo rows but turns into multi-second requests (or
    /// outright timeouts) once real teacher/student data accumulates.
    /// This adds a plain (non-unique) index on TeacherId to each of the 19
    /// tenant-scoped tables so Postgres can do an index scan instead. Cost on
    /// writes is negligible (one small B-tree entry per insert/update);
    /// benefit on reads is the whole point of this migration. Applied
    /// automatically via Database.Migrate() at startup, same as every other
    /// real migration in this project -- no manual SQL step needed.
    /// </remarks>
    public partial class AddTeacherIdIndexesForTenantIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(name: "IX_Groups_TeacherId", table: "Groups", column: "TeacherId");
            migrationBuilder.CreateIndex(name: "IX_Units_TeacherId", table: "Units", column: "TeacherId");
            migrationBuilder.CreateIndex(name: "IX_Lectures_TeacherId", table: "Lectures", column: "TeacherId");
            migrationBuilder.CreateIndex(name: "IX_Materials_TeacherId", table: "Materials", column: "TeacherId");
            migrationBuilder.CreateIndex(name: "IX_Notebooks_TeacherId", table: "Notebooks", column: "TeacherId");
            migrationBuilder.CreateIndex(name: "IX_Codes_TeacherId", table: "Codes", column: "TeacherId");
            migrationBuilder.CreateIndex(name: "IX_Notifications_TeacherId", table: "Notifications", column: "TeacherId");
            migrationBuilder.CreateIndex(name: "IX_Quizzes_TeacherId", table: "Quizzes", column: "TeacherId");
            migrationBuilder.CreateIndex(name: "IX_Assignments_TeacherId", table: "Assignments", column: "TeacherId");
            migrationBuilder.CreateIndex(name: "IX_BankQuestions_TeacherId", table: "BankQuestions", column: "TeacherId");
            migrationBuilder.CreateIndex(name: "IX_BankAttempts_TeacherId", table: "BankAttempts", column: "TeacherId");
            // Composite: these three are always queried by TeacherId (tenant
            // filter) AND StudentId ("this student's results") together, so
            // one composite index covers both that combination and any
            // TeacherId-only query (leftmost-column rule).
            migrationBuilder.CreateIndex(name: "IX_QuizResults_TeacherId_StudentId", table: "QuizResults", columns: new[] { "TeacherId", "StudentId" });
            migrationBuilder.CreateIndex(name: "IX_CenterQuizResults_TeacherId_StudentId", table: "CenterQuizResults", columns: new[] { "TeacherId", "StudentId" });
            migrationBuilder.CreateIndex(name: "IX_HomeworkResults_TeacherId_StudentId", table: "HomeworkResults", columns: new[] { "TeacherId", "StudentId" });
            migrationBuilder.CreateIndex(name: "IX_Attendances_TeacherId", table: "Attendances", column: "TeacherId");
            migrationBuilder.CreateIndex(name: "IX_AssignmentSubmissions_TeacherId", table: "AssignmentSubmissions", column: "TeacherId");
            migrationBuilder.CreateIndex(name: "IX_NotebookPayments_TeacherId", table: "NotebookPayments", column: "TeacherId");
            migrationBuilder.CreateIndex(name: "IX_StudentLectureUnlocks_TeacherId", table: "StudentLectureUnlocks", column: "TeacherId");
            migrationBuilder.CreateIndex(name: "IX_StudentUnitSubscriptions_TeacherId", table: "StudentUnitSubscriptions", column: "TeacherId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Groups_TeacherId", table: "Groups");
            migrationBuilder.DropIndex(name: "IX_Units_TeacherId", table: "Units");
            migrationBuilder.DropIndex(name: "IX_Lectures_TeacherId", table: "Lectures");
            migrationBuilder.DropIndex(name: "IX_Materials_TeacherId", table: "Materials");
            migrationBuilder.DropIndex(name: "IX_Notebooks_TeacherId", table: "Notebooks");
            migrationBuilder.DropIndex(name: "IX_Codes_TeacherId", table: "Codes");
            migrationBuilder.DropIndex(name: "IX_Notifications_TeacherId", table: "Notifications");
            migrationBuilder.DropIndex(name: "IX_Quizzes_TeacherId", table: "Quizzes");
            migrationBuilder.DropIndex(name: "IX_Assignments_TeacherId", table: "Assignments");
            migrationBuilder.DropIndex(name: "IX_BankQuestions_TeacherId", table: "BankQuestions");
            migrationBuilder.DropIndex(name: "IX_BankAttempts_TeacherId", table: "BankAttempts");
            migrationBuilder.DropIndex(name: "IX_QuizResults_TeacherId_StudentId", table: "QuizResults");
            migrationBuilder.DropIndex(name: "IX_CenterQuizResults_TeacherId_StudentId", table: "CenterQuizResults");
            migrationBuilder.DropIndex(name: "IX_HomeworkResults_TeacherId_StudentId", table: "HomeworkResults");
            migrationBuilder.DropIndex(name: "IX_Attendances_TeacherId", table: "Attendances");
            migrationBuilder.DropIndex(name: "IX_AssignmentSubmissions_TeacherId", table: "AssignmentSubmissions");
            migrationBuilder.DropIndex(name: "IX_NotebookPayments_TeacherId", table: "NotebookPayments");
            migrationBuilder.DropIndex(name: "IX_StudentLectureUnlocks_TeacherId", table: "StudentLectureUnlocks");
            migrationBuilder.DropIndex(name: "IX_StudentUnitSubscriptions_TeacherId", table: "StudentUnitSubscriptions");
        }
    }
}
