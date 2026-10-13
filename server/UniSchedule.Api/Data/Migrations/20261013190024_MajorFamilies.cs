using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UniSchedule.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MajorFamilies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Family",
                table: "Majors",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CourseDefaults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SemesterId = table.Column<int>(type: "INTEGER", nullable: false),
                    CourseId = table.Column<int>(type: "INTEGER", nullable: false),
                    MajorId = table.Column<int>(type: "INTEGER", nullable: true),
                    Family = table.Column<string>(type: "TEXT", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourseDefaults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CourseDefaults_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CourseDefaults_Majors_MajorId",
                        column: x => x.MajorId,
                        principalTable: "Majors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CourseDefaults_Semesters_SemesterId",
                        column: x => x.SemesterId,
                        principalTable: "Semesters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Majors_Family",
                table: "Majors",
                column: "Family");

            migrationBuilder.CreateIndex(
                name: "IX_CourseDefaults_CourseId",
                table: "CourseDefaults",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_CourseDefaults_MajorId",
                table: "CourseDefaults",
                column: "MajorId");

            migrationBuilder.CreateIndex(
                name: "IX_CourseDefaults_SemesterId_Year",
                table: "CourseDefaults",
                columns: new[] { "SemesterId", "Year" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CourseDefaults");

            migrationBuilder.DropIndex(
                name: "IX_Majors_Family",
                table: "Majors");

            migrationBuilder.DropColumn(
                name: "Family",
                table: "Majors");
        }
    }
}
