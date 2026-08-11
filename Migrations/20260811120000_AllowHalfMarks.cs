using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Lets a teacher enter half marks (e.g. 9.5) for manually-recorded
    /// center-quiz and homework results. Marks.Marks moves from integer to
    /// numeric(5,1) -- one decimal digit is enough for halves (9.5, 9.0
    /// still prints/round-trips as 9), and 5 total digits comfortably
    /// covers any total-marks value a teacher would realistically use.
    /// TotalMarks stays integer -- totals are always whole numbers.
    /// </remarks>
    public partial class AllowHalfMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "Marks",
                table: "CenterQuizResults",
                type: "numeric(5,1)",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<decimal>(
                name: "Marks",
                table: "HomeworkResults",
                type: "numeric(5,1)",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Truncates any half marks back to whole numbers on rollback.
            migrationBuilder.AlterColumn<int>(
                name: "Marks",
                table: "CenterQuizResults",
                type: "integer",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(5,1)");

            migrationBuilder.AlterColumn<int>(
                name: "Marks",
                table: "HomeworkResults",
                type: "integer",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(5,1)");
        }
    }
}
