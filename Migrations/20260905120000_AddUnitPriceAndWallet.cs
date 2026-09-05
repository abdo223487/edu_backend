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
    /// Hand-written, same pattern as AddLectureViewLimits/AddExternalBooks/
    /// AddLectureLinkFlags before it: both [DbContext] and [Migration] are
    /// required for Database.Migrate() to pick this up at startup, and no
    /// paired .Designer.cs is needed since this project doesn't rely on
    /// design-time model-diffing for hand-written migrations (see
    /// PendingModelChangesWarning suppression in Program.cs).
    ///
    /// Matches Scripts/2026-09-05_add_unit_price_and_wallet.sql exactly --
    /// run ONE of the two against a given database, never both.
    /// </remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260905120000_AddUnitPriceAndWallet")]
    public partial class AddUnitPriceAndWallet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Price",
                table: "Units",
                type: "decimal(10,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WalletBalance",
                table: "Students",
                type: "decimal(10,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "WalletTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false, defaultValue: "manual"),
                    Note = table.Column<string>(type: "text", nullable: true),
                    RelatedUnitId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByStaffId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalletTransactions_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_TeacherId",
                table: "WalletTransactions",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_StudentId",
                table: "WalletTransactions",
                column: "StudentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalletTransactions");

            migrationBuilder.DropColumn(
                name: "WalletBalance",
                table: "Students");

            migrationBuilder.DropColumn(
                name: "Price",
                table: "Units");
        }
    }
}
