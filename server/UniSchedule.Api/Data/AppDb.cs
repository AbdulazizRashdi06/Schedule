using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using UniSchedule.Api.Analytics;
using UniSchedule.Api.Planner;
using UniSchedule.Api.Teachers;

namespace UniSchedule.Api.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<BatchRepAssignment> BatchRepAssignments => Set<BatchRepAssignment>();
    public DbSet<PlannerWorkspace> PlannerWorkspaces => Set<PlannerWorkspace>();
    public DbSet<PlannerBatch> PlannerBatches => Set<PlannerBatch>();
    public DbSet<PlannerEvent> PlannerEvents => Set<PlannerEvent>();
    public DbSet<PlannerProposal> PlannerProposals => Set<PlannerProposal>();
    public DbSet<PlannerActivity> PlannerActivities => Set<PlannerActivity>();
    public DbSet<PlannerAiJob> PlannerAiJobs => Set<PlannerAiJob>();
    public DbSet<UsageDay> UsageDays => Set<UsageDay>();
    public DbSet<UsageHour> UsageHours => Set<UsageHour>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<LoginCode> LoginCodes => Set<LoginCode>();
    public DbSet<Semester> Semesters => Set<Semester>();
    public DbSet<Major> Majors => Set<Major>();
    public DbSet<MajorElective> MajorElectives => Set<MajorElective>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<CourseMajor> CourseMajors => Set<CourseMajor>();
    public DbSet<Section> Sections => Set<Section>();
    public DbSet<Meeting> Meetings => Set<Meeting>();
    public DbSet<Pick> Picks => Set<Pick>();
    public DbSet<UploadLog> UploadLogs => Set<UploadLog>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<CourseDefault> CourseDefaults => Set<CourseDefault>();
    public DbSet<CustomSlot> CustomSlots => Set<CustomSlot>();
    public DbSet<ClassNote> ClassNotes => Set<ClassNote>();
    public DbSet<NoteReport> NoteReports => Set<NoteReport>();
    public DbSet<FeedbackPrompt> FeedbackPrompts => Set<FeedbackPrompt>();
    public DbSet<SyncToken> SyncTokens => Set<SyncToken>();
    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<StudyGroup> StudyGroups => Set<StudyGroup>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<PendingSync> PendingSyncs => Set<PendingSync>();
    public DbSet<FeedbackResponse> FeedbackResponses => Set<FeedbackResponse>();
    public DbSet<ProblemReport> ProblemReports => Set<ProblemReport>();
    public DbSet<DayOff> DaysOff => Set<DayOff>();
    public DbSet<Absence> Absences => Set<Absence>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<Teacher> Teachers => Set<Teacher>();
    public DbSet<TeacherName> TeacherNames => Set<TeacherName>();
    public DbSet<TeacherSection> TeacherSections => Set<TeacherSection>();
    public DbSet<TeacherSeen> TeacherSeens => Set<TeacherSeen>();
    public DbSet<TeacherSlot> TeacherSlots => Set<TeacherSlot>();
    public DbSet<TeacherPushSubscription> TeacherPushSubscriptions => Set<TeacherPushSubscription>();
    public DbSet<OfficeHour> OfficeHours => Set<OfficeHour>();
    public DbSet<BlockedEmail> BlockedEmails => Set<BlockedEmail>();
    public DbSet<ClassChange> ClassChanges => Set<ClassChange>();
    public DbSet<TeachNotice> TeachNotices => Set<TeachNotice>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // SQLite can't compare or sort DateTimeOffset; stored as a number they can. All times are UTC.
        b.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        b.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        b.Properties<CourseType>().HaveConversion<string>().HaveMaxLength(16);
        b.Properties<AdminRole>().HaveConversion<string>().HaveMaxLength(16);
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<BatchRepAssignment>(e => {
            e.HasKey(x => x.StudentId);
            e.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<PlannerWorkspace>(e => {
            e.HasIndex(x => new { x.SemesterId, x.Family }).IsUnique();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<Semester>().WithMany().HasForeignKey(x => x.SemesterId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<PlannerBatch>(e => {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<PlannerWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<PlannerEvent>(e => {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.SemesterId);
            e.HasOne<Semester>().WithMany().HasForeignKey(x => x.SemesterId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<PlannerProposal>(e => {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.WorkspaceId, x.Status });
            e.HasOne<PlannerWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<PlannerEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.SetNull);
        });
        m.Entity<PlannerActivity>(e => {
            e.HasIndex(x => new { x.WorkspaceId, x.At });
            e.HasOne<PlannerWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<PlannerAiJob>(e => {
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasOne<PlannerWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<UsageDay>().HasKey(x => x.Day);
        m.Entity<UsageDay>().Property(x => x.Day).ValueGeneratedNever();
        m.Entity<UsageHour>().HasKey(x => new { x.Hour, x.Route, x.Method, x.Audience, x.Status });
        m.Entity<Student>(e =>
        {
            e.HasIndex(x => x.UniversityId).IsUnique();
            e.HasIndex(x => x.CalendarToken).IsUnique();
            e.HasOne(x => x.Major).WithMany().HasForeignKey(x => x.MajorId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Semester>().WithMany().HasForeignKey(x => x.SetupSemesterId).OnDelete(DeleteBehavior.SetNull);
        });
        m.Entity<AdminUser>().HasIndex(x => x.Username).IsUnique();
        m.Entity<LoginCode>().HasIndex(x => new { x.UniversityId, x.CreatedAt });
        m.Entity<Major>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.Family);
        });
        m.Entity<CourseDefault>(e =>
        {
            e.HasIndex(x => new { x.SemesterId, x.Year });
            e.HasOne<Semester>().WithMany().HasForeignKey(x => x.SemesterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Course).WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Major>().WithMany().HasForeignKey(x => x.MajorId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<MajorElective>(e =>
        {
            e.HasKey(x => new { x.MajorId, x.CourseCode });
            e.HasOne(x => x.Major).WithMany(x => x.Electives).HasForeignKey(x => x.MajorId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Course>(e =>
        {
            e.HasIndex(x => new { x.SemesterId, x.Code }).IsUnique();
            e.HasOne(x => x.Semester).WithMany(x => x.Courses).HasForeignKey(x => x.SemesterId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<CourseMajor>(e =>
        {
            e.HasKey(x => new { x.CourseId, x.MajorId });
            e.HasOne(x => x.Course).WithMany(x => x.Majors).HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Major).WithMany().HasForeignKey(x => x.MajorId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Section>(e =>
        {
            e.HasIndex(x => new { x.CourseId, x.Number }).IsUnique();
            e.HasOne(x => x.Course).WithMany(x => x.Sections).HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Meetings).WithOne().HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Pick>(e =>
        {
            e.HasIndex(x => new { x.StudentId, x.CourseId }).IsUnique();
            e.HasIndex(x => x.SectionId);
            e.HasOne(x => x.Student).WithMany(x => x.Picks).HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Course).WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Section).WithMany().HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.SetNull);
        });
        m.Entity<DayOff>(e =>
        {
            e.HasIndex(x => new { x.SemesterId, x.Date }).IsUnique();
            e.HasOne<Semester>().WithMany().HasForeignKey(x => x.SemesterId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Absence>(e =>
        {
            e.HasIndex(x => new { x.StudentId, x.CourseId, x.Date, x.Start }).IsUnique();
            e.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Course>().WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<PushSubscription>(e =>
        {
            e.HasIndex(x => x.Endpoint).IsUnique();
            e.HasIndex(x => x.StudentId);
            e.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<CustomSlot>(e =>
        {
            e.HasIndex(x => new { x.StudentId, x.SemesterId });
            e.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Semester>().WithMany().HasForeignKey(x => x.SemesterId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<ClassNote>(e =>
        {
            e.HasIndex(x => x.CourseId);
            e.HasIndex(x => new { x.StudentId, x.CreatedAt });
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Course).WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Section).WithMany().HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<NoteReport>(e =>
        {
            e.HasKey(x => new { x.NoteId, x.StudentId });
            e.HasOne<ClassNote>().WithMany().HasForeignKey(x => x.NoteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<SyncToken>(e => e.HasIndex(x => x.Hash).IsUnique());
        m.Entity<AppSetting>(e => e.HasKey(x => x.Key));
        m.Entity<Connection>(e => e.HasIndex(x => x.Kind).IsUnique());
        m.Entity<StudyGroup>(e => e.HasIndex(x => x.InviteCode).IsUnique());
        m.Entity<GroupMember>(e =>
        {
            e.HasIndex(x => new { x.GroupId, x.StudentId }).IsUnique();
            e.HasIndex(x => x.StudentId);
            e.HasOne(x => x.Group).WithMany(g => g.Members).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Enrollment>(e =>
        {
            e.HasIndex(x => new { x.SemesterId, x.UniversityId });
            e.HasOne<Semester>().WithMany().HasForeignKey(x => x.SemesterId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<PendingSync>(e => e.HasOne<Semester>().WithMany().HasForeignKey(x => x.SemesterId).OnDelete(DeleteBehavior.Cascade));
        m.Entity<FeedbackResponse>(e =>
        {
            e.HasIndex(x => new { x.PromptId, x.StudentId }).IsUnique();
            e.HasOne<FeedbackPrompt>().WithMany().HasForeignKey(x => x.PromptId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<ProblemReport>(e => e.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade));
        m.Entity<UploadLog>(e =>
        {
            e.HasIndex(x => x.At);
            e.HasOne<Semester>().WithMany().HasForeignKey(x => x.SemesterId).OnDelete(DeleteBehavior.Cascade);
        });

        // ---- Teachers: new tables only. Every foreign key has its delete rule here, so raw deletes can't fail on one. ----
        m.Entity<Teacher>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(80);
            e.Property(x => x.CreatedBy).HasMaxLength(64);
            e.Property(x => x.FrozenBy).HasMaxLength(64);
            e.Property(x => x.OfficeRoom).HasMaxLength(60);
            e.Property(x => x.ProfileNote).HasMaxLength(200);
            // SQLite allows many nulls in a unique index: student profiles have no email, staff accounts no student.
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.StudentId).IsUnique();
            e.HasIndex(x => x.CalendarToken).IsUnique();
            e.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<TeacherName>(e =>
        {
            e.Ignore(x => x.Holds);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.NameKey).HasMaxLength(120);
            e.Property(x => x.State).HasMaxLength(16);
            e.Property(x => x.How).HasMaxLength(16);
            e.Property(x => x.SuggestedName).HasMaxLength(120);
            e.Property(x => x.DecidedBy).HasMaxLength(64);
            e.HasIndex(x => new { x.TeacherId, x.NameKey }).IsUnique();
            e.HasIndex(x => x.State);
            // One account per timetable name.
            e.HasIndex(x => x.NameKey).IsUnique().HasFilter("\"State\" IN ('auto','approved')");
            e.HasOne<Teacher>().WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<TeacherSection>(e =>
        {
            e.Property(x => x.CourseCode).HasMaxLength(32);
            e.Property(x => x.SectionNumber).HasMaxLength(16);
            e.Property(x => x.Mode).HasMaxLength(8);
            e.Property(x => x.By).HasMaxLength(64);
            e.HasIndex(x => new { x.TeacherId, x.SemesterId, x.CourseCode, x.SectionNumber }).IsUnique();
            e.HasIndex(x => x.SectionId);
            e.HasOne<Teacher>().WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Semester>().WithMany().HasForeignKey(x => x.SemesterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Section>().WithMany().HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.SetNull);
        });
        m.Entity<TeacherSeen>(e =>
        {
            e.HasKey(x => new { x.TeacherId, x.SectionId });
            e.HasOne<Teacher>().WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Section>().WithMany().HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<TeacherSlot>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(60);
            e.Property(x => x.Place).HasMaxLength(60);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => new { x.TeacherId, x.SemesterId });
            e.HasOne<Teacher>().WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Semester>().WithMany().HasForeignKey(x => x.SemesterId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<TeacherPushSubscription>(e =>
        {
            e.HasIndex(x => x.Endpoint).IsUnique();
            e.HasIndex(x => x.TeacherId);
            e.HasOne<Teacher>().WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<OfficeHour>(e =>
        {
            e.Property(x => x.Room).HasMaxLength(60);
            e.HasIndex(x => x.TeacherId);
            e.HasOne<Teacher>().WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<BlockedEmail>(e =>
        {
            e.HasKey(x => x.Email);
            e.Property(x => x.Email).HasMaxLength(100);
            e.Property(x => x.BlockedBy).HasMaxLength(64);
        });
        m.Entity<ClassChange>(e =>
        {
            e.Property(x => x.CourseCode).HasMaxLength(32);
            e.Property(x => x.CourseName).HasMaxLength(200);
            e.Property(x => x.SectionNumber).HasMaxLength(16);
            e.Property(x => x.GroupId).HasMaxLength(32);
            e.Property(x => x.Kind).HasMaxLength(8);
            e.Property(x => x.SessionType).HasMaxLength(10);
            e.Property(x => x.OrigRoom).HasMaxLength(100);
            e.Property(x => x.NewRoom).HasMaxLength(100);
            e.Property(x => x.Note).HasMaxLength(200);
            e.Property(x => x.Tag).HasMaxLength(48);
            e.Property(x => x.ByKind).HasMaxLength(8);
            e.Property(x => x.ByLabel).HasMaxLength(100);
            e.Property(x => x.ByName).HasMaxLength(80);
            e.Property(x => x.OnBehalfOf).HasMaxLength(120);
            e.Property(x => x.NamedTeacher).HasMaxLength(120);
            e.Property(x => x.EndedBy).HasMaxLength(120);
            e.Property(x => x.EndReason).HasMaxLength(20);
            // One live change per session. Extra classes have no original session, so they aren't held to it.
            e.HasIndex(x => new { x.SectionId, x.OrigDate, x.OrigStart }).IsUnique()
                .HasFilter("\"EndedAt\" IS NULL AND \"Kind\" <> 'extra' AND \"SectionId\" IS NOT NULL");
            e.HasIndex(x => new { x.SemesterId, x.EndedAt });
            e.HasIndex(x => new { x.TeacherId, x.CreatedAt });
            e.HasIndex(x => x.GroupId);
            e.HasOne<Semester>().WithMany().HasForeignKey(x => x.SemesterId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Section>().WithMany().HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Teacher>().WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.SetNull);
        });
        m.Entity<TeachNotice>(e =>
        {
            e.Property(x => x.Audience).HasMaxLength(10);
            e.Property(x => x.GroupId).HasMaxLength(32);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Title).HasMaxLength(120);
            e.Property(x => x.Body).HasMaxLength(400);
            e.Property(x => x.Url).HasMaxLength(200);
            e.Property(x => x.Tag).HasMaxLength(64);
            e.Property(x => x.ByLabel).HasMaxLength(64);
            e.HasIndex(x => new { x.SentAt, x.Id });
            e.HasIndex(x => new { x.TeacherId, x.SeenAt });
            e.HasIndex(x => new { x.SectionId, x.Tag });
            e.HasOne<Section>().WithMany().HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Teacher>().WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
