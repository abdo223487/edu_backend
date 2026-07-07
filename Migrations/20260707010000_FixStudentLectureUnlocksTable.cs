using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    // BUGFIX: the original "AddStudentLectureUnlocks" migration was scaffolded
    // with a completely EMPTY Up()/Down() body — it got recorded in
    // __EFMigrationsHistory as applied without ever running a single SQL
    // statement, so the "StudentLectureUnlocks" table was never actually
    // created in the real database (Postgres error 42P01 on every query
    // touching it). Since that migration ID is already marked as applied,
    // fixing its body wouldn't do anything — EF skips migrations already in
    // history. This is a brand new migration (new id/timestamp) that actually
    // creates the table, matching the StudentLectureUnlock entity + the
    // unique (StudentId, LectureId) index configured in AppDbContext.
    public partial class FixStudentLectureUnlocksTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StudentLectureUnlocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    LectureId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentLectureUnlocks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StudentLectureUnlocks_StudentId_LectureId",
                table: "StudentLectureUnlocks",
                columns: new[] { "StudentId", "LectureId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StudentLectureUnlocks");
        }
    }
}
