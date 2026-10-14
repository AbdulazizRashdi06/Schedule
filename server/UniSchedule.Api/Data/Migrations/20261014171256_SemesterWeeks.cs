using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UniSchedule.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SemesterWeeks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastWeek",
                table: "Semesters",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "Week1Start",
                table: "Semesters",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastWeek",
                table: "Semesters");

            migrationBuilder.DropColumn(
                name: "Week1Start",
                table: "Semesters");
        }
    }
}
