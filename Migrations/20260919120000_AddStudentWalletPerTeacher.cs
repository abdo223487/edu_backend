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
    /// Hand-written, same pattern as AddUnitPriceAndWallet before it: both
    /// [DbContext] and [Migration] are required for Database.Migrate() to
    /// pick this up at startup, and no paired .Designer.cs is needed since
    /// this project doesn't rely on design-time model-diffing for
    /// hand-written migrations (see PendingModelChangesWarning suppression
    /// in Program.cs).
    ///
    /// BUGFIX (cross-tenant wallet leak): Student.WalletBalance was a single
    /// column shared by every teacher a student is linked to -- points added
    /// by one teacher were visible/spendable under any other teacher too.
    /// This creates StudentWallets (one row per StudentId+TeacherId, see
    /// StudentWallet in Entities.cs) and backfills each student's existing
    /// WalletBalance into the row for their LEGACY Group's teacher (the only
    /// teacher a pre-existing balance could actually be attributed to). The
    /// Student.WalletBalance column itself is left in place, unused, rather
    /// than dropped -- purely so this migration has no destructive Down-only
    /// data loss and old code paths that might still reference it during a
    /// rolling deploy don't hard-crash.
    ///
    /// Matches Scripts/2026-09-19_add_student_wallet_per_teacher.sql exactly
    /// -- run ONE of the two against a given database, never both.
    /// </remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260919120000_AddStudentWalletPerTeacher")]
    public partial class AddStudentWalletPerTeacher : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StudentWallets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(10,2)", nullable: false, defaultValue: 0m)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentWallets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudentWallets_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StudentWallets_StudentId_TeacherId",
                table: "StudentWallets",
                columns: new[] { "StudentId", "TeacherId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StudentWallets_TeacherId",
                table: "StudentWallets",
                column: "TeacherId");

            // ONE-TIME BACKFILL: attribute each student's existing
            // WalletBalance to their legacy Group's teacher -- the only
            // teacher relationship a pre-multi-tenant balance could have
            // come from. Students with a zero balance get no row (matches
            // WalletController treating "no row" as a balance of 0).
            migrationBuilder.Sql(@"
                INSERT INTO ""StudentWallets"" (""StudentId"", ""TeacherId"", ""Balance"")
                SELECT s.""Id"", g.""TeacherId"", s.""WalletBalance""
                FROM ""Students"" s
                JOIN ""Groups"" g ON g.""Id"" = s.""GroupId""
                WHERE s.""WalletBalance"" <> 0;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StudentWallets");
        }
    }
}
