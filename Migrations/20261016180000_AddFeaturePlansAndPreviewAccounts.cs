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
    ///  - FeaturePlans table (باقات الفيتشرز): name, price, included feature keys,
    ///    + free-text "available" / "blocked" lines the SuperAdmin writes.
    ///  - Teachers.PlanId             : the plan a teacher is on (NULL = custom / none)
    ///  - Teachers.ClosedAt           : PREVIEW accounts - closed / already converted
    ///  - Teachers.ConvertedTeacherId : PREVIEW accounts - the real teacher it became
    ///
    /// Requires 20261016120000_AddTeacherDisabledFeatures (Teachers.DisabledFeatures).
    /// Matches Scripts/2026-10-16_add_feature_plans_and_preview_accounts.sql exactly --
    /// run ONE of the two against a given database, never both.
    /// </remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261016180000_AddFeaturePlansAndPreviewAccounts")]
    public partial class AddFeaturePlansAndPreviewAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeaturePlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FeatureKeys = table.Column<string>(type: "text", nullable: true),
                    ExtraIncluded = table.Column<string>(type: "text", nullable: true),
                    ExtraExcluded = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeaturePlans", x => x.Id);
                });

            migrationBuilder.AddColumn<int>(
                name: "PlanId",
                table: "Teachers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAt",
                table: "Teachers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConvertedTeacherId",
                table: "Teachers",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Teachers_PlanId",
                table: "Teachers",
                column: "PlanId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Teachers_PlanId", table: "Teachers");
            migrationBuilder.DropColumn(name: "ConvertedTeacherId", table: "Teachers");
            migrationBuilder.DropColumn(name: "ClosedAt", table: "Teachers");
            migrationBuilder.DropColumn(name: "PlanId", table: "Teachers");
            migrationBuilder.DropTable(name: "FeaturePlans");
        }
    }
}
