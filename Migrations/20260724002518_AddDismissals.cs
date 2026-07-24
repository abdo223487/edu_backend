using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    public partial class AddDismissals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BankQuestions_Lessons_LessonId",
                table: "BankQuestions");

            migrationBuilder.DropForeignKey(
                name: "FK_BankQuestions_Units_UnitId",
                table: "BankQuestions");

            migrationBuilder.DropIndex(
                name: "IX_OnlineLessons_TeacherId",
                table: "OnlineLessons");

            migrationBuilder.CreateTable(
                name: "Dismissals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    UnitId = table.Column<int>(type: "integer", nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dismissals", x => x.Id);
                });

            migrationBuilder.AddForeignKey(
                name: "FK_BankQuestions_Lessons_LessonId",
                table: "BankQuestions",
                column: "LessonId",
                principalTable: "Lessons",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_BankQuestions_Units_UnitId",
                table: "BankQuestions",
                column: "UnitId",
                principalTable: "Units",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BankQuestions_Lessons_LessonId",
                table: "BankQuestions");

            migrationBuilder.DropForeignKey(
                name: "FK_BankQuestions_Units_UnitId",
                table: "BankQuestions");

            migrationBuilder.DropTable(
                name: "Dismissals");

            migrationBuilder.CreateIndex(
                name: "IX_OnlineLessons_TeacherId",
                table: "OnlineLessons",
                column: "TeacherId");

            migrationBuilder.AddForeignKey(
                name: "FK_BankQuestions_Lessons_LessonId",
                table: "BankQuestions",
                column: "LessonId",
                principalTable: "Lessons",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_BankQuestions_Units_UnitId",
                table: "BankQuestions",
                column: "UnitId",
                principalTable: "Units",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
