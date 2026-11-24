using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UniSchedule.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Teachers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BlockedEmails",
                columns: table => new
                {
                    Email = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BlockedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    BlockedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedEmails", x => x.Email);
                });

            migrationBuilder.CreateTable(
                name: "Teachers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    StudentId = table.Column<int>(type: "INTEGER", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    NameSkippedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FrozenAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FrozenBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SessionVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CalendarToken = table.Column<string>(type: "TEXT", nullable: true),
                    RemindMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    MuteChanges = table.Column<bool>(type: "INTEGER", nullable: false),
                    OfficeRoom = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    ProfileNote = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ShowEmail = table.Column<bool>(type: "INTEGER", nullable: false),
                    BusyAlertOn = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teachers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Teachers_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SemesterId = table.Column<int>(type: "INTEGER", nullable: false),
                    SectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    CourseCode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CourseName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SectionNumber = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    GroupId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    SessionType = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    OrigDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    OrigStart = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    OrigEnd = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    OrigRoom = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    NewDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    NewStart = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    NewEnd = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    NewRoom = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Tag = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    TeacherId = table.Column<int>(type: "INTEGER", nullable: true),
                    ByKind = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    ById = table.Column<int>(type: "INTEGER", nullable: false),
                    ByLabel = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ByName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    OnBehalfOf = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    NamedTeacher = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Flagged = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    EndedBy = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    EndReason = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassChanges_Sections_SectionId",
                        column: x => x.SectionId,
                        principalTable: "Sections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClassChanges_Semesters_SemesterId",
                        column: x => x.SemesterId,
                        principalTable: "Semesters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClassChanges_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Teachers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "OfficeHours",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeacherId = table.Column<int>(type: "INTEGER", nullable: false),
                    Day = table.Column<int>(type: "INTEGER", nullable: false),
                    Start = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    End = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    Room = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeHours", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OfficeHours_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Teachers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeacherNames",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeacherId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    NameKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    How = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Conflict = table.Column<bool>(type: "INTEGER", nullable: false),
                    SuggestedName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DecidedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DecidedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeacherNames", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeacherNames_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Teachers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeacherPushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeacherId = table.Column<int>(type: "INTEGER", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", nullable: false),
                    P256dh = table.Column<string>(type: "TEXT", nullable: false),
                    Auth = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeacherPushSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeacherPushSubscriptions_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Teachers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeacherSections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeacherId = table.Column<int>(type: "INTEGER", nullable: false),
                    SemesterId = table.Column<int>(type: "INTEGER", nullable: false),
                    SectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    CourseCode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SectionNumber = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    By = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeacherSections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeacherSections_Sections_SectionId",
                        column: x => x.SectionId,
                        principalTable: "Sections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TeacherSections_Semesters_SemesterId",
                        column: x => x.SemesterId,
                        principalTable: "Semesters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeacherSections_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Teachers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeacherSeens",
                columns: table => new
                {
                    TeacherId = table.Column<int>(type: "INTEGER", nullable: false),
                    SectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeacherSeens", x => new { x.TeacherId, x.SectionId });
                    table.ForeignKey(
                        name: "FK_TeacherSeens_Sections_SectionId",
                        column: x => x.SectionId,
                        principalTable: "Sections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeacherSeens_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Teachers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeacherSlots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeacherId = table.Column<int>(type: "INTEGER", nullable: false),
                    SemesterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Day = table.Column<int>(type: "INTEGER", nullable: false),
                    Start = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    End = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Place = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeacherSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeacherSlots_Semesters_SemesterId",
                        column: x => x.SemesterId,
                        principalTable: "Semesters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeacherSlots_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Teachers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeachNotices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Audience = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    SectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    TeacherId = table.Column<int>(type: "INTEGER", nullable: true),
                    GroupId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Tag = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ByLabel = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ClaimedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SeenAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeachNotices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeachNotices_Sections_SectionId",
                        column: x => x.SectionId,
                        principalTable: "Sections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TeachNotices_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Teachers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClassChanges_GroupId",
                table: "ClassChanges",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassChanges_SectionId_OrigDate_OrigStart",
                table: "ClassChanges",
                columns: new[] { "SectionId", "OrigDate", "OrigStart" },
                unique: true,
                filter: "\"EndedAt\" IS NULL AND \"Kind\" <> 'extra' AND \"SectionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClassChanges_SemesterId_EndedAt",
                table: "ClassChanges",
                columns: new[] { "SemesterId", "EndedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassChanges_TeacherId_CreatedAt",
                table: "ClassChanges",
                columns: new[] { "TeacherId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OfficeHours_TeacherId",
                table: "OfficeHours",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_TeacherNames_NameKey",
                table: "TeacherNames",
                column: "NameKey",
                unique: true,
                filter: "\"State\" IN ('auto','approved')");

            migrationBuilder.CreateIndex(
                name: "IX_TeacherNames_State",
                table: "TeacherNames",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_TeacherNames_TeacherId_NameKey",
                table: "TeacherNames",
                columns: new[] { "TeacherId", "NameKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeacherPushSubscriptions_Endpoint",
                table: "TeacherPushSubscriptions",
                column: "Endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeacherPushSubscriptions_TeacherId",
                table: "TeacherPushSubscriptions",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_Teachers_CalendarToken",
                table: "Teachers",
                column: "CalendarToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Teachers_Email",
                table: "Teachers",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Teachers_StudentId",
                table: "Teachers",
                column: "StudentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeacherSections_SectionId",
                table: "TeacherSections",
                column: "SectionId");

            migrationBuilder.CreateIndex(
                name: "IX_TeacherSections_SemesterId",
                table: "TeacherSections",
                column: "SemesterId");

            migrationBuilder.CreateIndex(
                name: "IX_TeacherSections_TeacherId_SemesterId_CourseCode_SectionNumber",
                table: "TeacherSections",
                columns: new[] { "TeacherId", "SemesterId", "CourseCode", "SectionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeacherSeens_SectionId",
                table: "TeacherSeens",
                column: "SectionId");

            migrationBuilder.CreateIndex(
                name: "IX_TeacherSlots_SemesterId",
                table: "TeacherSlots",
                column: "SemesterId");

            migrationBuilder.CreateIndex(
                name: "IX_TeacherSlots_TeacherId_SemesterId",
                table: "TeacherSlots",
                columns: new[] { "TeacherId", "SemesterId" });

            migrationBuilder.CreateIndex(
                name: "IX_TeachNotices_SectionId_Tag",
                table: "TeachNotices",
                columns: new[] { "SectionId", "Tag" });

            migrationBuilder.CreateIndex(
                name: "IX_TeachNotices_SentAt_Id",
                table: "TeachNotices",
                columns: new[] { "SentAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TeachNotices_TeacherId_SeenAt",
                table: "TeachNotices",
                columns: new[] { "TeacherId", "SeenAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlockedEmails");

            migrationBuilder.DropTable(
                name: "ClassChanges");

            migrationBuilder.DropTable(
                name: "OfficeHours");

            migrationBuilder.DropTable(
                name: "TeacherNames");

            migrationBuilder.DropTable(
                name: "TeacherPushSubscriptions");

            migrationBuilder.DropTable(
                name: "TeacherSections");

            migrationBuilder.DropTable(
                name: "TeacherSeens");

            migrationBuilder.DropTable(
                name: "TeacherSlots");

            migrationBuilder.DropTable(
                name: "TeachNotices");

            migrationBuilder.DropTable(
                name: "Teachers");
        }
    }
}
