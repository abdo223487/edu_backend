using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLectureExams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LectureExams",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: false),
                    LectureId = table.Column<int>(type: "integer", nullable: false),
                    DurationInMinutes = table.Column<int>(type: "integer", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LectureExams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LectureExamQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LectureExamId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Answer = table.Column<string>(type: "text", nullable: false),
                    Mark = table.Column<int>(type: "integer", nullable: false),
                    ChoicesCsv = table.Column<string>(type: "text", nullable: false),
                    ImageUrl = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LectureExamQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LectureExamQuestions_LectureExams_LectureExamId",
                        column: x => x.LectureExamId,
                        principalTable: "LectureExams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LectureExamStudentStarts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LectureExamId = table.Column<int>(type: "integer", nullable: false),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LectureExamStudentStarts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LectureExamResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LectureExamId = table.Column<int>(type: "integer", nullable: false),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    TotalMarks = table.Column<int>(type: "integer", nullable: false),
                    GradedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LectureExamResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LectureExamAnswers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LectureExamResultId = table.Column<int>(type: "integer", nullable: false),
                    QuestionId = table.Column<int>(type: "integer", nullable: false),
                    Answer = table.Column<string>(type: "text", nullable: false),
                    MarkAwarded = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LectureExamAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LectureExamAnswers_LectureExamResults_LectureExamResultId",
                        column: x => x.LectureExamResultId,
                        principalTable: "LectureExamResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LectureExams_TeacherId",
                table: "LectureExams",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_LectureExams_LectureId",
                table: "LectureExams",
                column: "LectureId");

            migrationBuilder.CreateIndex(
                name: "IX_LectureExamQuestions_LectureExamId",
                table: "LectureExamQuestions",
                column: "LectureExamId");

            migrationBuilder.CreateIndex(
                name: "IX_LectureExamStudentStarts_LectureExamId_StudentId",
                table: "LectureExamStudentStarts",
                columns: new[] { "LectureExamId", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LectureExamStudentStarts_TeacherId",
                table: "LectureExamStudentStarts",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_LectureExamResults_LectureExamId_StudentId",
                table: "LectureExamResults",
                columns: new[] { "LectureExamId", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LectureExamResults_TeacherId",
                table: "LectureExamResults",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_LectureExamAnswers_LectureExamResultId",
                table: "LectureExamAnswers",
                column: "LectureExamResultId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "LectureExamAnswers");
            migrationBuilder.DropTable(name: "LectureExamResults");
            migrationBuilder.DropTable(name: "LectureExamStudentStarts");
            migrationBuilder.DropTable(name: "LectureExamQuestions");
            migrationBuilder.DropTable(name: "LectureExams");
        }
    }
}
