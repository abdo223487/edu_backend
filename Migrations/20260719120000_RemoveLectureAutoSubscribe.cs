using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLectureAutoSubscribe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "AutoSubscribe" moved off Lecture: it's now decided per
            // attendance record (RecordAttendanceRequest.AutoSubscribe /
            // BulkAttendanceItem.AutoSubscribe) instead of being a fixed
            // flag on the lecture itself.
            migrationBuilder.Sql(@"
                ALTER TABLE ""Lectures""
                DROP COLUMN IF EXISTS ""AutoSubscribe"";
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoSubscribe",
                table: "Lectures",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
