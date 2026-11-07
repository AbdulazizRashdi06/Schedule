using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UniSchedule.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CalendarFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CalendarToken",
                table: "Students",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Students_CalendarToken",
                table: "Students",
                column: "CalendarToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Students_CalendarToken",
                table: "Students");

            migrationBuilder.DropColumn(
                name: "CalendarToken",
                table: "Students");
        }
    }
}
