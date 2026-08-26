using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAllowLateReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default true so every EXISTING quiz/assignment keeps behaving
            // exactly like before this feature existed (auto-review-on-late
            // access stays on unless a teacher explicitly turns it off).
            migrationBuilder.AddColumn<bool>(
                name: "AllowLateReview",
                table: "Quizzes",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowLateReview",
                table: "Assignments",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowLateReview",
                table: "AssignmentCenters",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowLateReview",
                table: "Quizzes");

            migrationBuilder.DropColumn(
                name: "AllowLateReview",
                table: "Assignments");

            migrationBuilder.DropColumn(
                name: "AllowLateReview",
                table: "AssignmentCenters");
        }
    }
}
