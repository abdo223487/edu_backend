using System;
using EduApi.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Hand-written (same pattern as AddContactMessages): both [DbContext] and
    /// [Migration] are required for Database.Migrate() to pick this up.
    ///
    /// "مصادر الحل":
    ///  - SolutionSources: per teacher + school year (Name)
    ///  - ContactMessages gets SolutionSourceId (FK, SET NULL on delete) + PageNumber + QuestionNumber
    ///
    /// Matches Scripts/2026-10-14_add_solution_sources.sql exactly -- run ONE of
    /// the two against a given database, never both.
    /// </remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261014230000_AddSolutionSources")]
    public partial class AddSolutionSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SolutionSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    SchoolYear = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolutionSources", x => x.Id);
                });

            migrationBuilder.AddColumn<int>(
                name: "SolutionSourceId",
                table: "ContactMessages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PageNumber",
                table: "ContactMessages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuestionNumber",
                table: "ContactMessages",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SolutionSources_TeacherId_SchoolYear",
                table: "SolutionSources",
                columns: new[] { "TeacherId", "SchoolYear" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactMessages_SolutionSourceId",
                table: "ContactMessages",
                column: "SolutionSourceId");

            migrationBuilder.AddForeignKey(
                name: "FK_ContactMessages_SolutionSources_SolutionSourceId",
                table: "ContactMessages",
                column: "SolutionSourceId",
                principalTable: "SolutionSources",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ContactMessages_SolutionSources_SolutionSourceId",
                table: "ContactMessages");

            migrationBuilder.DropIndex(
                name: "IX_ContactMessages_SolutionSourceId",
                table: "ContactMessages");

            migrationBuilder.DropColumn(name: "SolutionSourceId", table: "ContactMessages");
            migrationBuilder.DropColumn(name: "PageNumber", table: "ContactMessages");
            migrationBuilder.DropColumn(name: "QuestionNumber", table: "ContactMessages");

            migrationBuilder.DropTable(name: "SolutionSources");
        }
    }
}
