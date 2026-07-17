using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLectureAutoSubscribe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTemplate",
                table: "Codes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SourceCodeTemplateId",
                table: "Codes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TriggerLectureId",
                table: "Codes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Codes_TriggerLectureId",
                table: "Codes",
                column: "TriggerLectureId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Codes_TriggerLectureId",
                table: "Codes");

            migrationBuilder.DropColumn(
                name: "IsTemplate",
                table: "Codes");

            migrationBuilder.DropColumn(
                name: "SourceCodeTemplateId",
                table: "Codes");

            migrationBuilder.DropColumn(
                name: "TriggerLectureId",
                table: "Codes");
        }
    }
}
