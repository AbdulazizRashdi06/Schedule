namespace UniSchedule.Api.Teachers;

/// <summary>A staff account (signs in by email), or the teaching profile of a student who was given teaching rights.</summary>
public class Teacher
{
    public int Id { get; set; }
    /// <summary>Lower-case staff address. Null for a student's teaching profile.</summary>
    public string? Email { get; set; }
    /// <summary>Set only for a student teaching assistant.</summary>
    public int? StudentId { get; set; }
    /// <summary>Typed by the owner for a student. Empty for staff: their shown name is worked out (<see cref="TeacherNames.Shown"/>).</summary>
    public string DisplayName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    /// <summary>Who granted teaching rights (student profiles).</summary>
    public string? CreatedBy { get; set; }
    /// <summary>Pressed "My name isn't here".</summary>
    public DateTimeOffset? NameSkippedAt { get; set; }
    /// <summary>Frozen by staff: no class changes, no added sections, no office-hours edits.</summary>
    public DateTimeOffset? FrozenAt { get; set; }
    public string? FrozenBy { get; set; }
    /// <summary>Goes up to end this account's sign-ins everywhere.</summary>
    public int SessionVersion { get; set; }
    /// <summary>Secret in the teacher's calendar feed link. Null until they first ask for the link.</summary>
    public string? CalendarToken { get; set; }
    /// <summary>Minutes before each class to send a reminder; null = no reminders.</summary>
    public int? RemindMinutes { get; set; }
    /// <summary>No notification when the university timetable changes one of their classes.</summary>
    public bool MuteChanges { get; set; }
    public string OfficeRoom { get; set; } = "";
    public string ProfileNote { get; set; } = "";
    /// <summary>Students see the sign-in address. Off unless the teacher turns it on.</summary>
    public bool ShowEmail { get; set; }
    /// <summary>The Oman day the "more than 5 changes" alert was raised.</summary>
    public DateOnly? BusyAlertOn { get; set; }
}

/// <summary>A link between an account and a timetable name. The link is the name's text, so it survives re-imports and new semesters.</summary>
public class TeacherName
{
    public const string Auto = "auto", Approved = "approved", Pending = "pending", Rejected = "rejected";
    public const string ByEmail = "email", Picked = "picked", ByStaff = "staff";

    public int Id { get; set; }
    public int TeacherId { get; set; }
    /// <summary>As written on the timetable.</summary>
    public string Name { get; set; } = "";
    /// <summary><see cref="TeacherNames.NameKey"/> of <see cref="Name"/>.</summary>
    public string NameKey { get; set; } = "";
    /// <summary>auto, approved, pending or rejected. Only auto and approved give classes.</summary>
    public string State { get; set; } = Pending;
    /// <summary>email (matched at sign-in), picked (chosen by the teacher) or staff (attached in the dashboard).</summary>
    public string How { get; set; } = Picked;
    /// <summary>Another account holds or wants this name.</summary>
    public bool Conflict { get; set; }
    /// <summary>A new spelling staff can accept with one tap.</summary>
    public string? SuggestedName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }

    /// <summary>Whether this link gives the account the name's classes.</summary>
    public bool Holds => State is Auto or Approved;
}

/// <summary>A section a teacher added, or hid. One semester only.</summary>
public class TeacherSection
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public int SemesterId { get; set; }
    /// <summary>Null = the university removed it; it re-attaches by text if it returns.</summary>
    public int? SectionId { get; set; }
    public string CourseCode { get; set; } = "";
    public string SectionNumber { get; set; } = "";
    /// <summary>added or hidden.</summary>
    public string Mode { get; set; } = "";
    /// <summary>Null when the teacher did it; the owner when it was done through "view as".</summary>
    public string? By { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>When a teacher last opened a class (clears its "Changed" badge).</summary>
public class TeacherSeen
{
    public int TeacherId { get; set; }
    public int SectionId { get; set; }
    public DateTimeOffset SeenAt { get; set; }
}

/// <summary>A teacher's own slot: every week on <see cref="Day"/>, or once on <see cref="Date"/>. Private to them.</summary>
public class TeacherSlot
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public int SemesterId { get; set; }
    public string Title { get; set; } = "";
    /// <summary>0 = Sunday … 6 = Saturday (the date's weekday for a dated slot).</summary>
    public int Day { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
    public DateOnly? Date { get; set; }
    public string Place { get; set; } = "";
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A browser or installed app that accepts a staff account's notifications (Web Push).</summary>
public class TeacherPushSubscription
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A weekly office hour. Not tied to a semester.</summary>
public class OfficeHour
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    /// <summary>0 = Sunday … 6 = Saturday.</summary>
    public int Day { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
    public string Room { get; set; } = "";
}

/// <summary>A staff email that was removed and can't sign in until staff unblock it. Kept through "Delete all data".</summary>
public class BlockedEmail
{
    public string Email { get; set; } = "";
    public DateTimeOffset BlockedAt { get; set; }
    public string BlockedBy { get; set; } = "";
}

/// <summary>
/// A teacher's change to one session of one section: cancelled, moved, another room, or an extra class.
/// Live while <see cref="EndedAt"/> is null. The rows are also the change log.
/// </summary>
public class ClassChange
{
    public int Id { get; set; }
    public int SemesterId { get; set; }
    /// <summary>Null once the section is gone; the row is always ended before that.</summary>
    public int? SectionId { get; set; }
    public string CourseCode { get; set; } = "";
    public string CourseName { get; set; } = "";
    public string SectionNumber { get; set; } = "";
    /// <summary>One teacher action on one real class (several course codes) shares it.</summary>
    public string GroupId { get; set; } = "";
    /// <summary>cancel, move, room or extra.</summary>
    public string Kind { get; set; } = "";
    /// <summary>lecture, lab or tutorial.</summary>
    public string SessionType { get; set; } = "";
    /// <summary>The session key, with <see cref="SectionId"/>. Null for an extra class.</summary>
    public DateOnly? OrigDate { get; set; }
    public TimeOnly? OrigStart { get; set; }
    /// <summary>Snapshot of the university session, refreshed on re-import. Never part of the key.</summary>
    public TimeOnly? OrigEnd { get; set; }
    public string? OrigRoom { get; set; }
    /// <summary>Null for a cancel.</summary>
    public DateOnly? NewDate { get; set; }
    public TimeOnly? NewStart { get; set; }
    public TimeOnly? NewEnd { get; set; }
    /// <summary>Null for a cancel. "" = to be announced.</summary>
    public string? NewRoom { get; set; }
    public string? Note { get; set; }
    /// <summary>The notification tag for this session. A replacement copies it.</summary>
    public string Tag { get; set; } = "";
    /// <summary>The teaching account it was made as. Null when an owner viewed a name nobody has claimed, or the account was removed.</summary>
    public int? TeacherId { get; set; }
    /// <summary>Who was signed in: teacher, student or admin, and that table's row id.</summary>
    public string ByKind { get; set; } = "";
    public int ById { get; set; }
    /// <summary>For the log: staff email, university ID or dashboard username.</summary>
    public string ByLabel { get; set; } = "";
    /// <summary>What students see: "Raed Bolbol", or "Classy staff" for an owner's "view as".</summary>
    public string ByName { get; set; } = "";
    /// <summary>The name an owner was viewing as.</summary>
    public string? OnBehalfOf { get; set; }
    /// <summary>The section's instructor at the time.</summary>
    public string NamedTeacher { get; set; } = "";
    /// <summary>The maker was not the class's named teacher (or it was made through "view as").</summary>
    public bool Flagged { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>For the log only: teacher:{email}, assistant:{university ID}, staff:{name} or system.</summary>
    public string? EndedBy { get; set; }
    /// <summary>undone, undone-by-staff, undone-on-remove, replaced, dropped-moved, dropped-removed, dropped-day-off or merged.</summary>
    public string? EndReason { get; set; }
}

/// <summary>The outbox for notifications about teacher changes, and the teacher's in-app inbox.</summary>
public class TeachNotice
{
    public const string Students = "students", ToTeacher = "teacher", Staff = "staff";

    public int Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>students (everyone who picked <see cref="SectionId"/>), teacher (<see cref="TeacherId"/>) or staff (student owners).</summary>
    public string Audience { get; set; } = "";
    public int? SectionId { get; set; }
    public int? TeacherId { get; set; }
    /// <summary>The change it is about.</summary>
    public string? GroupId { get; set; }
    public string Kind { get; set; } = "";
    /// <summary>The final text, written when the event happens.</summary>
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Url { get; set; } = "";
    public string Tag { get; set; } = "";
    /// <summary>The owner who edited the account through "view as" (staff-edit rows).</summary>
    public string? ByLabel { get; set; }
    /// <summary>Not pushed after this (the class has started).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    /// <summary>Teacher inbox only.</summary>
    public DateTimeOffset? SeenAt { get; set; }
}
