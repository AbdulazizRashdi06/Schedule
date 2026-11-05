using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UniSchedule.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AggregateUsageAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UsageDays",
                columns: table => new
                {
                    Day = table.Column<long>(type: "INTEGER", nullable: false),
                    ActiveUsers = table.Column<int>(type: "INTEGER", nullable: false),
                    PeakUsers = table.Column<int>(type: "INTEGER", nullable: false),
                    PeakAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstSample = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSample = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageDays", x => x.Day);
                });

            migrationBuilder.CreateTable(
                name: "UsageHours",
                columns: table => new
                {
                    Hour = table.Column<long>(type: "INTEGER", nullable: false),
                    Route = table.Column<string>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", nullable: false),
                    Audience = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Requests = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalMs = table.Column<double>(type: "REAL", nullable: false),
                    MaxMs = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageHours", x => new { x.Hour, x.Route, x.Method, x.Audience, x.Status });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UsageDays");

            migrationBuilder.DropTable(
                name: "UsageHours");
        }
    }
}
