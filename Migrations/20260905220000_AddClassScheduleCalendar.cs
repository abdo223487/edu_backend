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
    /// Hand-written, same pattern as AddClassSchedule before it. The earlier
    /// ClassScheduleDays table (weekly pattern) is left in place untouched --
    /// this just adds the new ClassScheduleEntries table (real per-date
    /// calendar) that ScheduleController now actually uses.
    /// Matches Scripts/2026-09-05_add_class_schedule_calendar.sql exactly --
    /// run ONE of the two against a given database, never both.
    /// </remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260905220000_AddClassScheduleCalendar")]
    public partial class AddClassScheduleCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClassScheduleEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SchoolYear = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: true),
                    TeacherId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassScheduleEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClassScheduleEntries_TeacherId",
                table: "ClassScheduleEntries",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassScheduleEntries_TeacherId_SchoolYear_Date",
                table: "ClassScheduleEntries",
                columns: new[] { "TeacherId", "SchoolYear", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassScheduleEntries");
        }
    }
}
