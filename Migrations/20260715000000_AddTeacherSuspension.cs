using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// SUPERADMIN CONTROL: lets a SuperAdmin suspend a teacher (block login +
    /// block every still-valid request for that tenant via
    /// TenantSuspensionMiddleware) without deleting any of their data.
    /// </remarks>
    public partial class AddTeacherSuspension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSuspended",
                table: "Teachers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "SuspendedAt",
                table: "Teachers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuspensionReason",
                table: "Teachers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSuspended",
                table: "Teachers");

            migrationBuilder.DropColumn(
                name: "SuspendedAt",
                table: "Teachers");

            migrationBuilder.DropColumn(
                name: "SuspensionReason",
                table: "Teachers");
        }
    }
}
