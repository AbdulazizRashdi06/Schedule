using System.ComponentModel.DataAnnotations.Schema;

namespace UniSchedule.Api.Data;

public enum CourseType { Core, LS, Elective }

public enum AdminRole { Rsa, Owner }

/// <summary>Anyone with a valid university ID. Owners are students with <see cref="IsOwner"/>.</summary>
public class Student
{
    public int Id { get; set; }
    /// <summary>"24-0161".</summary>
    public string UniversityId { get; set; } = "";
    public string Email { get; set; } = "";
    public bool IsOwner { get; set; }
    public int? MajorId { get; set; }
    public Major? Major { get; set; }
    /// <summary>Study year: 0 is the foundation year, then 1–5.</summary>
    public int? Year { get; set; }

    /// <summary>The semester the setup steps below belong to (null while nothing is published).</summary>
    public int? SetupSemesterId { get; set; }
    /// <summary>major, year, core, ls, electives, done, or waiting (nothing published yet).</summary>
    public string? SetupStep { get; set; }
    public DateTimeOffset? SetupCompletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public List<Pick> Picks { get; set; } = [];
}

/// <summary>Dashboard account with a username and password (RSA staff, or an owner without a student ID).</summary>
public class AdminUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public AdminRole Role { get; set; }
    public bool MustChangePassword { get; set; } = true;
    public int FailedLogins { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}

public class LoginCode
{
    public int Id { get; set; }
    public string UniversityId { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public int Attempts { get; set; }
}

public class Semester
{
    public int Id { get; set; }
    /// <summary>"Fall 2026".</summary>
    public string Name { get; set; } = "";
    /// <summary>Fall, Spring or Summer.</summary>
    public string Term { get; set; } = "";
    public int Year { get; set; }
    /// <summary>The published semester students see. At most one.</summary>
    public bool IsCurrent { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>The Sunday teaching week 1 starts on. Null = the first week with dated classes.</summary>
    public DateOnly? Week1Start { get; set; }
    /// <summary>The last teaching week's number, e.g. 14. Null = the last week with dated classes.</summary>
    public int? LastWeek { get; set; }
    public List<Course> Courses { get; set; } = [];

    /// <summary>The academic year starts in the fall: Spring 2027 belongs to 2026/27.</summary>
    [NotMapped]
    public int AcademicStartYear => Term.Equals("Fall", StringComparison.OrdinalIgnoreCase) ? Year : Year - 1;
}

public class Major
{
    public int Id { get; set; }
    /// <summary>Short code used in uploads, e.g. "CS".</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Family code, e.g. "COMP" for AI, CS and CYS. Majors in one family share their courses; null = on its own.</summary>
    public string? Family { get; set; }
    /// <summary>Electives this major allows on top of those the upload lists for it (by course code, so they survive semesters).</summary>
    public List<MajorElective> Electives { get; set; } = [];
}

public class MajorElective
{
    public int MajorId { get; set; }
    public Major Major { get; set; } = null!;
    public string CourseCode { get; set; } = "";
}

public class Course
{
    public int Id { get; set; }
    public int SemesterId { get; set; }
    public Semester Semester { get; set; } = null!;
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public CourseType Type { get; set; }
    /// <summary>Study year a core course belongs to (0 = foundation); null for LS and most electives.</summary>
    public int? Year { get; set; }
    public int? Credits { get; set; }
    public List<CourseMajor> Majors { get; set; } = [];
    public List<Section> Sections { get; set; } = [];
}

public class CourseMajor
{
    public int CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public int MajorId { get; set; }
    public Major Major { get; set; } = null!;
}

public class Section
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public Course Course { get; set; } = null!;
    /// <summary>"1", "2", "L1".</summary>
    public string Number { get; set; } = "";
    public string Instructor { get; set; } = "";
    public List<Meeting> Meetings { get; set; } = [];
    /// <summary>Last edit of a published section; drives the "Changed" badge.</summary>
    public DateTimeOffset? ChangedAt { get; set; }
    public string? ChangeSummary { get; set; }
}

public class Meeting
{
    public int Id { get; set; }
    public int SectionId { get; set; }
    /// <summary>0 = Sunday … 6 = Saturday.</summary>
    public int Day { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
    public string Room { get; set; } = "";
    /// <summary>A class on one date only (timetables that change week to week). Null = every week on <see cref="Day"/>.</summary>
    public DateOnly? Date { get; set; }
}

/// <summary>A course a student takes this semester, and the section they picked (null until they pick one).</summary>
public class Pick
{
    public int Id { get; set; }
    public int StudentId { get; set; }
    public Student Student { get; set; } = null!;
    public int CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public int? SectionId { get; set; }
    public Section? Section { get; set; }
    /// <summary>The picked section was removed by a re-upload; the student needs to pick again.</summary>
    public bool SectionRemoved { get; set; }
    /// <summary>When the student last opened this class (clears the "Changed" badge).</summary>
    public DateTimeOffset? SeenAt { get; set; }
}

/// <summary>
/// A course ticked for students of one major (or one family) and study year when setup starts.
/// A major's own defaults for a year replace its family's; with neither, the course's Year and majors from the upload decide.
/// </summary>
public class CourseDefault
{
    public int Id { get; set; }
    public int SemesterId { get; set; }
    public int CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public int? MajorId { get; set; }
    /// <summary>Set instead of <see cref="MajorId"/> for a family default.</summary>
    public string? Family { get; set; }
    public int Year { get; set; }
}

/// <summary>One applied upload, for "Recent uploads" on the dashboard home.</summary>
public class UploadLog
{
    public int Id { get; set; }
    public int SemesterId { get; set; }
    /// <summary>The uploaded file's name, or "Pasted rows".</summary>
    public string FileName { get; set; } = "";
    /// <summary>Username or university ID of whoever applied it.</summary>
    public string By { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
}
