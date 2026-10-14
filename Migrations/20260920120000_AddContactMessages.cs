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
    /// Hand-written (same pattern as AddStudentWalletPerTeacher): both [DbContext]
    /// and [Migration] are required for Database.Migrate() to pick this up.
    ///
    /// Creates the student <-> teacher contact feature:
    ///  - ContactMessages: the opening question of a conversation (text + optional image)
    ///  - ContactReplies: follow-up messages from the teacher OR the student (text + optional image)
    /// See ContactMessage / ContactReply in Entities.cs.
    ///
    /// Matches Scripts/2026-09-20_add_contact_messages.sql exactly -- run ONE of
    /// the two against a given database, never both.
    /// </remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260920120000_AddContactMessages")]
    public partial class AddContactMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContactMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    ImageUrl = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastActivityAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsReadByTeacher = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContactMessages_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContactReplies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContactMessageId = table.Column<int>(type: "integer", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    IsFromTeacher = table.Column<bool>(type: "boolean", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    ImageUrl = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactReplies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContactReplies_ContactMessages_ContactMessageId",
                        column: x => x.ContactMessageId,
                        principalTable: "ContactMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContactMessages_StudentId",
                table: "ContactMessages",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactMessages_TeacherId_StudentId",
                table: "ContactMessages",
                columns: new[] { "TeacherId", "StudentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactMessages_TeacherId_IsReadByTeacher_LastActivityAt",
                table: "ContactMessages",
                columns: new[] { "TeacherId", "IsReadByTeacher", "LastActivityAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactReplies_ContactMessageId_CreatedAt",
                table: "ContactReplies",
                columns: new[] { "ContactMessageId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactReplies_TeacherId",
                table: "ContactReplies",
                column: "TeacherId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ContactReplies");
            migrationBuilder.DropTable(name: "ContactMessages");
        }
    }
}
