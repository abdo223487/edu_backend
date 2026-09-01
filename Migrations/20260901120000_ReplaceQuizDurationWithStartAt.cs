using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Replaces Quiz.DurationInMinutes (a per-attempt countdown length) with an
    /// explicit Quiz.StartAt (when the exam window opens), matching how
    /// Assignment already works with a single Deadline. The exam's effective
    /// length is now simply Deadline - StartAt for everyone, instead of a
    /// fixed minutes value applied to whenever each student happened to open it.
    ///
    /// Existing rows are backfilled with StartAt = Deadline - DurationInMinutes,
    /// i.e. the window that would have produced the same length under the old
    /// model, so no already-scheduled exam silently changes length.
    /// </remarks>
    public partial class ReplaceQuizDurationWithStartAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "StartAt",
                table: "Quizzes",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "'-infinity'");

            migrationBuilder.Sql(
                @"UPDATE ""Quizzes""
                  SET ""StartAt"" = ""Deadline"" - (""DurationInMinutes"" * INTERVAL '1 minute');");

            migrationBuilder.DropColumn(
                name: "DurationInMinutes",
                table: "Quizzes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DurationInMinutes",
                table: "Quizzes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                @"UPDATE ""Quizzes""
                  SET ""DurationInMinutes"" = GREATEST(1, CEIL(EXTRACT(EPOCH FROM (""Deadline"" - ""StartAt"")) / 60)::int);");

            migrationBuilder.DropColumn(
                name: "StartAt",
                table: "Quizzes");
        }
    }
}
