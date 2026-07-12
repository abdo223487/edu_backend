using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// CORRECTNESS + PERFORMANCE FIX: Lecture/Assignment/Notification/Quiz
    /// stored their Group (and Assignment's Unit) associations as a
    /// comma-separated string (e.g. "3,12,105") and queries filtered with a
    /// substring Contains() on it. That has two real problems, not just a
    /// speed one:
    ///   1. It can't use an index -- Postgres translates Contains() to
    ///      LIKE '%...%', which is always a full scan.
    ///   2. It's WRONG: Contains("1") matches inside "10", "21", "1,2" --
    ///      filtering for group 1 could silently include rows for group 10.
    /// This adds a real join table for each CSV column (indexed, exact
    /// match) and backfills it by parsing the existing CSV data. The CSV
    /// columns themselves are left in place as the source of truth for
    /// writes -- AppDbContext.SaveChangesAsync now keeps the join tables in
    /// sync automatically every time a Lecture/Assignment/Notification/Quiz
    /// is created or updated, so no existing controller code had to change
    /// except the read-side filters. Applied automatically via
    /// Database.Migrate() at startup -- no manual SQL step needed.
    /// </remarks>
    public partial class AddGroupUnitLinkTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LectureGroupLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LectureId = table.Column<int>(type: "integer", nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LectureGroupLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LectureGroupLinks_Lectures_LectureId",
                        column: x => x.LectureId,
                        principalTable: "Lectures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.CreateIndex(name: "IX_LectureGroupLinks_LectureId_GroupId", table: "LectureGroupLinks", columns: new[] { "LectureId", "GroupId" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_LectureGroupLinks_GroupId", table: "LectureGroupLinks", column: "GroupId");

            migrationBuilder.CreateTable(
                name: "AssignmentGroupLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssignmentId = table.Column<int>(type: "integer", nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentGroupLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssignmentGroupLinks_Assignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "Assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.CreateIndex(name: "IX_AssignmentGroupLinks_AssignmentId_GroupId", table: "AssignmentGroupLinks", columns: new[] { "AssignmentId", "GroupId" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_AssignmentGroupLinks_GroupId", table: "AssignmentGroupLinks", column: "GroupId");

            migrationBuilder.CreateTable(
                name: "AssignmentUnitLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssignmentId = table.Column<int>(type: "integer", nullable: false),
                    UnitId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentUnitLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssignmentUnitLinks_Assignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "Assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.CreateIndex(name: "IX_AssignmentUnitLinks_AssignmentId_UnitId", table: "AssignmentUnitLinks", columns: new[] { "AssignmentId", "UnitId" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_AssignmentUnitLinks_UnitId", table: "AssignmentUnitLinks", column: "UnitId");

            migrationBuilder.CreateTable(
                name: "NotificationGroupLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NotificationId = table.Column<int>(type: "integer", nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationGroupLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationGroupLinks_Notifications_NotificationId",
                        column: x => x.NotificationId,
                        principalTable: "Notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.CreateIndex(name: "IX_NotificationGroupLinks_NotificationId_GroupId", table: "NotificationGroupLinks", columns: new[] { "NotificationId", "GroupId" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_NotificationGroupLinks_GroupId", table: "NotificationGroupLinks", column: "GroupId");

            migrationBuilder.CreateTable(
                name: "QuizGroupLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    QuizId = table.Column<int>(type: "integer", nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuizGroupLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuizGroupLinks_Quizzes_QuizId",
                        column: x => x.QuizId,
                        principalTable: "Quizzes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.CreateIndex(name: "IX_QuizGroupLinks_QuizId_GroupId", table: "QuizGroupLinks", columns: new[] { "QuizId", "GroupId" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_QuizGroupLinks_GroupId", table: "QuizGroupLinks", column: "GroupId");

            // Backfill: parse each existing CSV column into the new join
            // table. unnest(string_to_array(...)) turns "3,12,105" into three
            // rows; NULLIF guards against an empty-string CSV (no groups)
            // producing a bogus single NULL row, which CAST would reject.
            migrationBuilder.Sql(
                @"INSERT INTO ""LectureGroupLinks"" (""LectureId"", ""GroupId"")
                  SELECT s.""Id"", CAST(v AS integer)
                  FROM ""Lectures"" s, unnest(string_to_array(NULLIF(s.""GroupIdsCsv"", ''), ',')) AS v
                  WHERE v <> ''
                  ON CONFLICT DO NOTHING;");

            migrationBuilder.Sql(
                @"INSERT INTO ""AssignmentGroupLinks"" (""AssignmentId"", ""GroupId"")
                  SELECT s.""Id"", CAST(v AS integer)
                  FROM ""Assignments"" s, unnest(string_to_array(NULLIF(s.""GroupIdsCsv"", ''), ',')) AS v
                  WHERE v <> ''
                  ON CONFLICT DO NOTHING;");

            migrationBuilder.Sql(
                @"INSERT INTO ""AssignmentUnitLinks"" (""AssignmentId"", ""UnitId"")
                  SELECT s.""Id"", CAST(v AS integer)
                  FROM ""Assignments"" s, unnest(string_to_array(NULLIF(s.""UnitIdsCsv"", ''), ',')) AS v
                  WHERE v <> ''
                  ON CONFLICT DO NOTHING;");

            migrationBuilder.Sql(
                @"INSERT INTO ""NotificationGroupLinks"" (""NotificationId"", ""GroupId"")
                  SELECT s.""Id"", CAST(v AS integer)
                  FROM ""Notifications"" s, unnest(string_to_array(NULLIF(s.""GroupIdsCsv"", ''), ',')) AS v
                  WHERE v <> ''
                  ON CONFLICT DO NOTHING;");

            migrationBuilder.Sql(
                @"INSERT INTO ""QuizGroupLinks"" (""QuizId"", ""GroupId"")
                  SELECT s.""Id"", CAST(v AS integer)
                  FROM ""Quizzes"" s, unnest(string_to_array(NULLIF(s.""GroupIdsCsv"", ''), ',')) AS v
                  WHERE v <> ''
                  ON CONFLICT DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "LectureGroupLinks");
            migrationBuilder.DropTable(name: "AssignmentGroupLinks");
            migrationBuilder.DropTable(name: "AssignmentUnitLinks");
            migrationBuilder.DropTable(name: "NotificationGroupLinks");
            migrationBuilder.DropTable(name: "QuizGroupLinks");
        }
    }
}
