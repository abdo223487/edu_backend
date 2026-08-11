using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAssignmentCenters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Materials_NotebookId",
                table: "Materials");

            migrationBuilder.AlterColumn<decimal>(
                name: "Marks",
                table: "HomeworkResults",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(5,1)");

            migrationBuilder.AlterColumn<decimal>(
                name: "Marks",
                table: "CenterQuizResults",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(5,1)");

            migrationBuilder.CreateTable(
                name: "AssignmentCenters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: false),
                    SchoolYear = table.Column<int>(type: "integer", nullable: true),
                    Deadline = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GroupIdsCsv = table.Column<string>(type: "text", nullable: false),
                    UnitIdsCsv = table.Column<string>(type: "text", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentCenters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssignmentCenterSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    AssignmentCenterId = table.Column<int>(type: "integer", nullable: false),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    TotalMarks = table.Column<int>(type: "integer", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentCenterSubmissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssignmentCenterGroupLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssignmentCenterId = table.Column<int>(type: "integer", nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentCenterGroupLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssignmentCenterGroupLinks_AssignmentCenters_AssignmentCent~",
                        column: x => x.AssignmentCenterId,
                        principalTable: "AssignmentCenters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssignmentCenterQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssignmentCenterId = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Answer = table.Column<string>(type: "text", nullable: false),
                    Mark = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentCenterQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssignmentCenterQuestions_AssignmentCenters_AssignmentCente~",
                        column: x => x.AssignmentCenterId,
                        principalTable: "AssignmentCenters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssignmentCenterUnitLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssignmentCenterId = table.Column<int>(type: "integer", nullable: false),
                    UnitId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentCenterUnitLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssignmentCenterUnitLinks_AssignmentCenters_AssignmentCente~",
                        column: x => x.AssignmentCenterId,
                        principalTable: "AssignmentCenters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssignmentCenterAnswers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssignmentCenterSubmissionId = table.Column<int>(type: "integer", nullable: false),
                    QuestionId = table.Column<int>(type: "integer", nullable: false),
                    Answer = table.Column<string>(type: "text", nullable: false),
                    MarkAwarded = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentCenterAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssignmentCenterAnswers_AssignmentCenterSubmissions_Assignm~",
                        column: x => x.AssignmentCenterSubmissionId,
                        principalTable: "AssignmentCenterSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentCenterAnswers_AssignmentCenterSubmissionId",
                table: "AssignmentCenterAnswers",
                column: "AssignmentCenterSubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentCenterGroupLinks_AssignmentCenterId_GroupId",
                table: "AssignmentCenterGroupLinks",
                columns: new[] { "AssignmentCenterId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentCenterGroupLinks_GroupId",
                table: "AssignmentCenterGroupLinks",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentCenterQuestions_AssignmentCenterId",
                table: "AssignmentCenterQuestions",
                column: "AssignmentCenterId");

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentCenters_TeacherId",
                table: "AssignmentCenters",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentCenterSubmissions_AssignmentCenterId_StudentId",
                table: "AssignmentCenterSubmissions",
                columns: new[] { "AssignmentCenterId", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentCenterSubmissions_TeacherId",
                table: "AssignmentCenterSubmissions",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentCenterUnitLinks_AssignmentCenterId_UnitId",
                table: "AssignmentCenterUnitLinks",
                columns: new[] { "AssignmentCenterId", "UnitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentCenterUnitLinks_UnitId",
                table: "AssignmentCenterUnitLinks",
                column: "UnitId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssignmentCenterAnswers");

            migrationBuilder.DropTable(
                name: "AssignmentCenterGroupLinks");

            migrationBuilder.DropTable(
                name: "AssignmentCenterQuestions");

            migrationBuilder.DropTable(
                name: "AssignmentCenterUnitLinks");

            migrationBuilder.DropTable(
                name: "AssignmentCenterSubmissions");

            migrationBuilder.DropTable(
                name: "AssignmentCenters");

            migrationBuilder.AlterColumn<decimal>(
                name: "Marks",
                table: "HomeworkResults",
                type: "numeric(5,1)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "Marks",
                table: "CenterQuizResults",
                type: "numeric(5,1)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.CreateIndex(
                name: "IX_Materials_NotebookId",
                table: "Materials",
                column: "NotebookId");
        }
    }
}
