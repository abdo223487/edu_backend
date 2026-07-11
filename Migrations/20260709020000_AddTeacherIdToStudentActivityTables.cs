using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// MULTI-TENANT SECURITY HARDENING: Attendances, AssignmentSubmissions,
    /// NotebookPayments, StudentLectureUnlocks and StudentUnitSubscriptions
    /// were only ever filtered by StudentId, with no TeacherId of their own
    /// and no global query filter. Every current call site happens to be
    /// safe today because it joins against an already tenant-filtered table
    /// (Lecture/Assignment/Notebook/Unit) first, but nothing enforced that at
    /// the database level -- same class of bug fixed for QuizResult /
    /// CenterQuizResult / HomeworkResult in AddTeacherIdToResultsTables. This
    /// adds TeacherId to each table and backfills it from the table each
    /// row's real tenant can be derived from unambiguously:
    ///   - Attendances: from the parent Lecture.
    ///   - AssignmentSubmissions: from the parent Assignment.
    ///   - NotebookPayments: from the parent Notebook.
    ///   - StudentLectureUnlocks: from the parent Lecture.
    ///   - StudentUnitSubscriptions: from the parent Unit.
    /// Applied automatically via Database.Migrate() at startup, same as every
    /// other real migration in this project -- no manual SQL step needed.
    /// </remarks>
    public partial class AddTeacherIdToStudentActivityTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TeacherId",
                table: "Attendances",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TeacherId",
                table: "AssignmentSubmissions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TeacherId",
                table: "NotebookPayments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TeacherId",
                table: "StudentLectureUnlocks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TeacherId",
                table: "StudentUnitSubscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill from each row's parent (tenant-owned) entity.
            migrationBuilder.Sql(
                @"UPDATE ""Attendances"" a
                  SET ""TeacherId"" = l.""TeacherId""
                  FROM ""Lectures"" l
                  WHERE l.""Id"" = a.""LectureId"";");

            migrationBuilder.Sql(
                @"UPDATE ""AssignmentSubmissions"" s
                  SET ""TeacherId"" = asg.""TeacherId""
                  FROM ""Assignments"" asg
                  WHERE asg.""Id"" = s.""AssignmentId"";");

            migrationBuilder.Sql(
                @"UPDATE ""NotebookPayments"" p
                  SET ""TeacherId"" = n.""TeacherId""
                  FROM ""Notebooks"" n
                  WHERE n.""Id"" = p.""NotebookId"";");

            migrationBuilder.Sql(
                @"UPDATE ""StudentLectureUnlocks"" u
                  SET ""TeacherId"" = l.""TeacherId""
                  FROM ""Lectures"" l
                  WHERE l.""Id"" = u.""LectureId"";");

            migrationBuilder.Sql(
                @"UPDATE ""StudentUnitSubscriptions"" sub
                  SET ""TeacherId"" = un.""TeacherId""
                  FROM ""Units"" un
                  WHERE un.""Id"" = sub.""UnitId"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TeacherId",
                table: "Attendances");

            migrationBuilder.DropColumn(
                name: "TeacherId",
                table: "AssignmentSubmissions");

            migrationBuilder.DropColumn(
                name: "TeacherId",
                table: "NotebookPayments");

            migrationBuilder.DropColumn(
                name: "TeacherId",
                table: "StudentLectureUnlocks");

            migrationBuilder.DropColumn(
                name: "TeacherId",
                table: "StudentUnitSubscriptions");
        }
    }
}
