using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    public partial class AddNotebookPaymentLectureId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LectureId",
                table: "NotebookPayments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotebookPayments_LectureId",
                table: "NotebookPayments",
                column: "LectureId");

            migrationBuilder.AddForeignKey(
                name: "FK_NotebookPayments_Lectures_LectureId",
                table: "NotebookPayments",
                column: "LectureId",
                principalTable: "Lectures",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NotebookPayments_Lectures_LectureId",
                table: "NotebookPayments");

            migrationBuilder.DropIndex(
                name: "IX_NotebookPayments_LectureId",
                table: "NotebookPayments");

            migrationBuilder.DropColumn(
                name: "LectureId",
                table: "NotebookPayments");
        }
    }
}
