using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UniSchedule.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProblemReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProblemReports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StudentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Page = table.Column<string>(type: "TEXT", nullable: true),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProblemReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProblemReports_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProblemReports_StudentId",
                table: "ProblemReports",
                column: "StudentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProblemReports");
        }
    }
}
