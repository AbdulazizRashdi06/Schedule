using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace UniSchedule.Api.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
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
    public DbSet<CourseDefault> CourseDefaults => Set<CourseDefault>();
    public DbSet<CustomSlot> CustomSlots => Set<CustomSlot>();
    public DbSet<ClassNote> ClassNotes => Set<ClassNote>();
    public DbSet<NoteReport> NoteReports => Set<NoteReport>();

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
        m.Entity<Student>(e =>
        {
            e.HasIndex(x => x.UniversityId).IsUnique();
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
        m.Entity<UploadLog>(e =>
        {
            e.HasIndex(x => x.At);
            e.HasOne<Semester>().WithMany().HasForeignKey(x => x.SemesterId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
