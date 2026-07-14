using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// BUGFIX: IsSuspended/IsCancelled used to live as a single flag on Student,
    /// which meant a student suspended/cancelled by ONE teacher came back
    /// suspended/cancelled for EVERY teacher they're linked to, even though
    /// teachers are otherwise fully isolated tenants. Moves both flags onto
    /// StudentGroupMembership instead, so suspend/cancel is a decision scoped to
    /// one teacher's relationship with the student. Existing values are copied
    /// onto every membership row for that student so current suspensions/
    /// cancellations aren't silently lost -- teachers can then reactivate on a
    /// per-relationship basis going forward.
    /// </remarks>
    public partial class MoveSuspendCancelToMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSuspended",
                table: "StudentGroupMemberships",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsCancelled",
                table: "StudentGroupMemberships",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill: carry each student's old global flags onto every membership
            // row they currently have, so nothing already suspended/cancelled
            // silently becomes active. From this point forward each teacher
            // manages the flag independently via their own membership row.
            migrationBuilder.Sql(
                @"UPDATE ""StudentGroupMemberships"" m
                  SET ""IsSuspended"" = s.""IsSuspended"", ""IsCancelled"" = s.""IsCancelled""
                  FROM ""Students"" s
                  WHERE m.""StudentId"" = s.""Id"";");

            migrationBuilder.DropColumn(
                name: "IsSuspended",
                table: "Students");

            migrationBuilder.DropColumn(
                name: "IsCancelled",
                table: "Students");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSuspended",
                table: "Students",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsCancelled",
                table: "Students",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                @"UPDATE ""Students"" s
                  SET ""IsSuspended"" = COALESCE((SELECT bool_or(m.""IsSuspended"") FROM ""StudentGroupMemberships"" m WHERE m.""StudentId"" = s.""Id""), false),
                      ""IsCancelled"" = COALESCE((SELECT bool_or(m.""IsCancelled"") FROM ""StudentGroupMemberships"" m WHERE m.""StudentId"" = s.""Id""), false);");

            migrationBuilder.DropColumn(
                name: "IsSuspended",
                table: "StudentGroupMemberships");

            migrationBuilder.DropColumn(
                name: "IsCancelled",
                table: "StudentGroupMemberships");
        }
    }
}
