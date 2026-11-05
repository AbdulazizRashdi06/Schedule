using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UniSchedule.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SharedSemesterPlanner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BatchRepAssignments",
                columns: table => new
                {
                    StudentId = table.Column<int>(type: "INTEGER", nullable: false),
                    HomeFamily = table.Column<string>(type: "TEXT", nullable: false),
                    AssignedBy = table.Column<string>(type: "TEXT", nullable: false),
                    AssignedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BatchRepAssignments", x => x.StudentId);
                    table.ForeignKey(
                        name: "FK_BatchRepAssignments_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlannerEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SemesterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Withdrawn = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlannerEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlannerEvents_Semesters_SemesterId",
                        column: x => x.SemesterId,
                        principalTable: "Semesters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlannerWorkspaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SemesterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Family = table.Column<string>(type: "TEXT", nullable: false),
                    DailyExamWarning = table.Column<int>(type: "INTEGER", nullable: false),
                    WeeklyExamWarning = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlannerWorkspaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlannerWorkspaces_Semesters_SemesterId",
                        column: x => x.SemesterId,
                        principalTable: "Semesters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlannerActivities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    EventId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProposalId = table.Column<int>(type: "INTEGER", nullable: true),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    Explanation = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlannerActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlannerActivities_PlannerWorkspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "PlannerWorkspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlannerAiJobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    StudentId = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerAccount = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    InputJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlannerAiJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlannerAiJobs_PlannerWorkspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "PlannerWorkspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlannerBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MajorId = table.Column<int>(type: "INTEGER", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlannerBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlannerBatches_PlannerWorkspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "PlannerWorkspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlannerProposals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    EventId = table.Column<int>(type: "INTEGER", nullable: true),
                    BaseEventVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlannerProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlannerProposals_PlannerEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "PlannerEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PlannerProposals_PlannerWorkspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "PlannerWorkspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlannerActivities_WorkspaceId_At",
                table: "PlannerActivities",
                columns: new[] { "WorkspaceId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PlannerAiJobs_Status_CreatedAt",
                table: "PlannerAiJobs",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlannerAiJobs_WorkspaceId",
                table: "PlannerAiJobs",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannerBatches_WorkspaceId",
                table: "PlannerBatches",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannerEvents_SemesterId",
                table: "PlannerEvents",
                column: "SemesterId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannerProposals_EventId",
                table: "PlannerProposals",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannerProposals_WorkspaceId_Status",
                table: "PlannerProposals",
                columns: new[] { "WorkspaceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PlannerWorkspaces_SemesterId_Family",
                table: "PlannerWorkspaces",
                columns: new[] { "SemesterId", "Family" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BatchRepAssignments");

            migrationBuilder.DropTable(
                name: "PlannerActivities");

            migrationBuilder.DropTable(
                name: "PlannerAiJobs");

            migrationBuilder.DropTable(
                name: "PlannerBatches");

            migrationBuilder.DropTable(
                name: "PlannerProposals");

            migrationBuilder.DropTable(
                name: "PlannerEvents");

            migrationBuilder.DropTable(
                name: "PlannerWorkspaces");
        }
    }
}
