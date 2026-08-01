using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    public partial class AddMaterialNotebookId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NotebookId",
                table: "Materials",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Materials_NotebookId",
                table: "Materials",
                column: "NotebookId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Materials_NotebookId",
                table: "Materials");

            migrationBuilder.DropColumn(
                name: "NotebookId",
                table: "Materials");
        }
    }
}
