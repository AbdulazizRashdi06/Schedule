using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UniSchedule.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OptInEngagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EngagementConsents",
                columns: table => new
                {
                    StudentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChangedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngagementConsents", x => x.StudentId);
                    table.ForeignKey(
                        name: "FK_EngagementConsents_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EngagementSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    StudentId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ActiveMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngagementSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EngagementSessions_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EngagementScreens",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    Screen = table.Column<string>(type: "TEXT", nullable: false),
                    ReportedMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ActiveMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Views = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngagementScreens", x => new { x.SessionId, x.Screen });
                    table.ForeignKey(
                        name: "FK_EngagementScreens_EngagementSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "EngagementSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EngagementSessions_StartedAt",
                table: "EngagementSessions",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EngagementSessions_StudentId_LastAt",
                table: "EngagementSessions",
                columns: new[] { "StudentId", "LastAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EngagementConsents");

            migrationBuilder.DropTable(
                name: "EngagementScreens");

            migrationBuilder.DropTable(
                name: "EngagementSessions");
        }
    }
}
