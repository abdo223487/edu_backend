using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Adds the StudentGroupMemberships join table so a Student can belong to
    /// Groups under more than one Teacher (tenant) at once. Applied
    /// automatically via Database.Migrate() at startup -- no manual SQL step
    /// needed.
    /// </remarks>
    public partial class AddStudentGroupMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StudentGroupMemberships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "(now() AT TIME ZONE 'utc')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentGroupMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudentGroupMemberships_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StudentGroupMemberships_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StudentGroupMemberships_StudentId_GroupId",
                table: "StudentGroupMemberships",
                columns: new[] { "StudentId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StudentGroupMemberships_GroupId",
                table: "StudentGroupMemberships",
                column: "GroupId");

            // Backfill: every existing student gets an automatic membership
            // for their current group (zero behavior change).
            migrationBuilder.Sql(
                @"INSERT INTO ""StudentGroupMemberships"" (""StudentId"", ""GroupId"")
                  SELECT s.""Id"", s.""GroupId""
                  FROM ""Students"" s
                  WHERE NOT EXISTS (
                      SELECT 1 FROM ""StudentGroupMemberships"" m
                      WHERE m.""StudentId"" = s.""Id"" AND m.""GroupId"" = s.""GroupId""
                  );");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StudentGroupMemberships");
        }
    }
}
