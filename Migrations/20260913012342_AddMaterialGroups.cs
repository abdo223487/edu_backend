using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    public partial class AddMaterialGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Price",
                table: "Units",
                type: "numeric(10,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WalletBalance",
                table: "Students",
                type: "numeric(10,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "GroupIdsCsv",
                table: "Materials",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ExternalBookId",
                table: "Lectures",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireLinkAssignment",
                table: "Lectures",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireLinkExam",
                table: "Lectures",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ViewLimit",
                table: "Lectures",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalBookIdsCsv",
                table: "Codes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "AssignmentCenterStudentOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssignmentCenterId = table.Column<int>(type: "integer", nullable: false),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    ForceReview = table.Column<bool>(type: "boolean", nullable: false),
                    ReopenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentCenterStudentOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClassScheduleDays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SchoolYear = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: true),
                    TeacherId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassScheduleDays", x => x.Id);
                });

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

            migrationBuilder.CreateTable(
                name: "ExternalBooks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SchoolYear = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: true),
                    ImageUrl = table.Column<string>(type: "text", nullable: true),
                    UnitId = table.Column<int>(type: "integer", nullable: true),
                    TeacherId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalBooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalBooks_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "LectureAssignmentResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LectureAssignmentId = table.Column<int>(type: "integer", nullable: false),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    TotalMarks = table.Column<int>(type: "integer", nullable: false),
                    GradedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LectureAssignmentResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LectureAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: false),
                    LectureId = table.Column<int>(type: "integer", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LectureAssignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LectureAssignmentStudentOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LectureAssignmentId = table.Column<int>(type: "integer", nullable: false),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    ForceReview = table.Column<bool>(type: "boolean", nullable: false),
                    ReopenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LectureAssignmentStudentOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LectureExamStudentOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LectureExamId = table.Column<int>(type: "integer", nullable: false),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    ForceReview = table.Column<bool>(type: "boolean", nullable: false),
                    ReopenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LectureExamStudentOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaterialGroupLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MaterialId = table.Column<int>(type: "integer", nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialGroupLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaterialGroupLinks_Materials_MaterialId",
                        column: x => x.MaterialId,
                        principalTable: "Materials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StudentExternalBookSubscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    ExternalBookId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentExternalBookSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StudentLectureViewUsages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    LectureId = table.Column<int>(type: "integer", nullable: false),
                    ViewsUsed = table.Column<int>(type: "integer", nullable: false),
                    ExtraViews = table.Column<int>(type: "integer", nullable: false),
                    LastViewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentLectureViewUsages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StudentRegistrationRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    PhoneNumber = table.Column<string>(type: "text", nullable: false),
                    ParentPhoneNumber = table.Column<string>(type: "text", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false),
                    SchoolYear = table.Column<int>(type: "integer", nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    AccessCode = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RejectionReason = table.Column<string>(type: "text", nullable: true),
                    CreatedStudentId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentRegistrationRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WalletTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StudentId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    RelatedUnitId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByStaffId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TeacherId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalletTransactions_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalBookLessons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExternalBookId = table.Column<int>(type: "integer", nullable: false),
                    LessonIndex = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ImageUrl = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalBookLessons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalBookLessons_ExternalBooks_ExternalBookId",
                        column: x => x.ExternalBookId,
                        principalTable: "ExternalBooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LectureAssignmentAnswers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LectureAssignmentResultId = table.Column<int>(type: "integer", nullable: false),
                    QuestionId = table.Column<int>(type: "integer", nullable: false),
                    Answer = table.Column<string>(type: "text", nullable: false),
                    MarkAwarded = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LectureAssignmentAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LectureAssignmentAnswers_LectureAssignmentResults_LectureAs~",
                        column: x => x.LectureAssignmentResultId,
                        principalTable: "LectureAssignmentResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LectureAssignmentQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LectureAssignmentId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Answer = table.Column<string>(type: "text", nullable: false),
                    Mark = table.Column<int>(type: "integer", nullable: false),
                    ChoicesCsv = table.Column<string>(type: "text", nullable: false),
                    ImageUrl = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LectureAssignmentQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LectureAssignmentQuestions_LectureAssignments_LectureAssign~",
                        column: x => x.LectureAssignmentId,
                        principalTable: "LectureAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentCenterStudentOverrides_AssignmentCenterId_Student~",
                table: "AssignmentCenterStudentOverrides",
                columns: new[] { "AssignmentCenterId", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentCenterStudentOverrides_TeacherId",
                table: "AssignmentCenterStudentOverrides",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassScheduleDays_TeacherId",
                table: "ClassScheduleDays",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassScheduleDays_TeacherId_SchoolYear_DayOfWeek",
                table: "ClassScheduleDays",
                columns: new[] { "TeacherId", "SchoolYear", "DayOfWeek" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassScheduleEntries_TeacherId",
                table: "ClassScheduleEntries",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassScheduleEntries_TeacherId_SchoolYear_Date",
                table: "ClassScheduleEntries",
                columns: new[] { "TeacherId", "SchoolYear", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalBookLessons_ExternalBookId",
                table: "ExternalBookLessons",
                column: "ExternalBookId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalBooks_TeacherId",
                table: "ExternalBooks",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalBooks_UnitId",
                table: "ExternalBooks",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_LectureAssignmentAnswers_LectureAssignmentResultId",
                table: "LectureAssignmentAnswers",
                column: "LectureAssignmentResultId");

            migrationBuilder.CreateIndex(
                name: "IX_LectureAssignmentQuestions_LectureAssignmentId",
                table: "LectureAssignmentQuestions",
                column: "LectureAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_LectureAssignmentResults_LectureAssignmentId_StudentId",
                table: "LectureAssignmentResults",
                columns: new[] { "LectureAssignmentId", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LectureAssignmentResults_TeacherId",
                table: "LectureAssignmentResults",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_LectureAssignments_LectureId",
                table: "LectureAssignments",
                column: "LectureId");

            migrationBuilder.CreateIndex(
                name: "IX_LectureAssignments_TeacherId",
                table: "LectureAssignments",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_LectureAssignmentStudentOverrides_LectureAssignmentId_Stude~",
                table: "LectureAssignmentStudentOverrides",
                columns: new[] { "LectureAssignmentId", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LectureAssignmentStudentOverrides_TeacherId",
                table: "LectureAssignmentStudentOverrides",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_LectureExamStudentOverrides_LectureExamId_StudentId",
                table: "LectureExamStudentOverrides",
                columns: new[] { "LectureExamId", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LectureExamStudentOverrides_TeacherId",
                table: "LectureExamStudentOverrides",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialGroupLinks_GroupId",
                table: "MaterialGroupLinks",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialGroupLinks_MaterialId_GroupId",
                table: "MaterialGroupLinks",
                columns: new[] { "MaterialId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StudentExternalBookSubscriptions_StudentId_ExternalBookId",
                table: "StudentExternalBookSubscriptions",
                columns: new[] { "StudentId", "ExternalBookId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StudentExternalBookSubscriptions_TeacherId",
                table: "StudentExternalBookSubscriptions",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentLectureViewUsages_StudentId_LectureId",
                table: "StudentLectureViewUsages",
                columns: new[] { "StudentId", "LectureId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StudentLectureViewUsages_TeacherId",
                table: "StudentLectureViewUsages",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentRegistrationRequests_Id_AccessCode",
                table: "StudentRegistrationRequests",
                columns: new[] { "Id", "AccessCode" });

            migrationBuilder.CreateIndex(
                name: "IX_StudentRegistrationRequests_TeacherId",
                table: "StudentRegistrationRequests",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_StudentId",
                table: "WalletTransactions",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_TeacherId",
                table: "WalletTransactions",
                column: "TeacherId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssignmentCenterStudentOverrides");

            migrationBuilder.DropTable(
                name: "ClassScheduleDays");

            migrationBuilder.DropTable(
                name: "ClassScheduleEntries");

            migrationBuilder.DropTable(
                name: "ExternalBookLessons");

            migrationBuilder.DropTable(
                name: "LectureAssignmentAnswers");

            migrationBuilder.DropTable(
                name: "LectureAssignmentQuestions");

            migrationBuilder.DropTable(
                name: "LectureAssignmentStudentOverrides");

            migrationBuilder.DropTable(
                name: "LectureExamStudentOverrides");

            migrationBuilder.DropTable(
                name: "MaterialGroupLinks");

            migrationBuilder.DropTable(
                name: "StudentExternalBookSubscriptions");

            migrationBuilder.DropTable(
                name: "StudentLectureViewUsages");

            migrationBuilder.DropTable(
                name: "StudentRegistrationRequests");

            migrationBuilder.DropTable(
                name: "WalletTransactions");

            migrationBuilder.DropTable(
                name: "ExternalBooks");

            migrationBuilder.DropTable(
                name: "LectureAssignmentResults");

            migrationBuilder.DropTable(
                name: "LectureAssignments");

            migrationBuilder.DropColumn(
                name: "Price",
                table: "Units");

            migrationBuilder.DropColumn(
                name: "WalletBalance",
                table: "Students");

            migrationBuilder.DropColumn(
                name: "GroupIdsCsv",
                table: "Materials");

            migrationBuilder.DropColumn(
                name: "ExternalBookId",
                table: "Lectures");

            migrationBuilder.DropColumn(
                name: "RequireLinkAssignment",
                table: "Lectures");

            migrationBuilder.DropColumn(
                name: "RequireLinkExam",
                table: "Lectures");

            migrationBuilder.DropColumn(
                name: "ViewLimit",
                table: "Lectures");

            migrationBuilder.DropColumn(
                name: "ExternalBookIdsCsv",
                table: "Codes");
        }
    }
}
