using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduApi.Migrations
{
    /// <inheritdoc />
    public partial class AddDeletedItemLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeletedItemLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: true),
                    EntityType = table.Column<string>(type: "text", nullable: false),
                    EntityId = table.Column<int>(type: "integer", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    SnapshotJson = table.Column<string>(type: "text", nullable: false),
                    DeletedByRole = table.Column<string>(type: "text", nullable: false),
                    DeletedByUserId = table.Column<int>(type: "integer", nullable: true),
                    DeletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsRestored = table.Column<bool>(type: "boolean", nullable: false),
                    RestoredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeletedItemLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeletedItemLogs_TenantId_DeletedAtUtc",
                table: "DeletedItemLogs",
                columns: new[] { "TenantId", "DeletedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeletedItemLogs");
        }
    }
}
