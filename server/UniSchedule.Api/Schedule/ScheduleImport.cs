using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;

namespace UniSchedule.Api.Schedule;

public record ParsedMeeting(int Day, TimeOnly Start, TimeOnly End, string Room);
public record ParsedSection(string Number, string Instructor, List<ParsedMeeting> Meetings);
public record ParsedCourse(string Code, string Name, CourseType Type, int? Year, int? Credits, List<string> Majors, List<ParsedSection> Sections);
public record RowIssue(int Line, string Message);

/// <summary>Validated upload, grouped by course and section.</summary>
public record ParsedSchedule(List<ParsedCourse> Courses, List<RowIssue> Errors, List<RowIssue> Warnings, int RowCount);

public record Change(string Kind, string Code, string? Section, string Detail);

public record ImportPreview(
    int Rows, int Courses, int Sections,
    List<RowIssue> Errors, List<RowIssue> Warnings, List<string> NewMajors,
    List<Change> Added, List<Change> Updated, List<Change> Removed,
    int StudentsAffected, bool CanApply);

public static class ScheduleImport
{
    public static CourseType? ParseType(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "core" or "c" or "required" or "major core" or "compulsory" => CourseType.Core,
        "ls" or "life skills" or "lifeskills" or "life skill" or "life-skills" or "general" => CourseType.LS,
        "elective" or "e" or "el" or "major elective" or "free elective" => CourseType.Elective,
        _ => null,
    };

    public static List<string> SplitMajors(string raw) =>
        raw.Split([';', ',', '/', '|', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static ParsedSchedule Parse(List<ScheduleRow> rows)
    {
        var errors = new List<RowIssue>();
        var warnings = new List<RowIssue>();
        var courses = new Dictionary<string, (ParsedCourse Course, Dictionary<string, ParsedSection> Sections)>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var rowErrors = new List<string>();
            if (row.Code.Length == 0) rowErrors.Add("course code is empty");
            if (row.Section.Length == 0) rowErrors.Add("section is empty");
            var type = row.Type.Length == 0 ? CourseType.Core : ParseType(row.Type);
            if (type is null) rowErrors.Add($"type \"{row.Type}\" should be Core, LS or Elective");
            var days = Timeslots.ParseDays(row.Days);
            if (days is null) rowErrors.Add(row.Days.Length == 0 ? "days are empty" : $"can't read days \"{row.Days}\"");
            var time = Timeslots.ParseRangeLoose(row.Time);
            if (time is null) rowErrors.Add(row.Time.Length == 0 ? "time is empty" : $"can't read time \"{row.Time}\"");
            int? year = null;
            if (row.Year.Length > 0)
            {
                var digits = new string(row.Year.Where(char.IsAsciiDigit).ToArray());
                if (int.TryParse(digits, out var y) && y is >= 1 and <= 6) year = y;
                else rowErrors.Add($"year \"{row.Year}\" should be 1–5");
            }
            int? credits = int.TryParse(row.Credits, out var cr) && cr is > 0 and < 20 ? cr : null;
            var majors = SplitMajors(row.Majors);
            if (type == CourseType.Core && majors.Count == 0) rowErrors.Add("a core course needs a major");
            if (type == CourseType.Core && year is null) warnings.Add(new(row.Line, $"{row.Code}: core course has no year, so it won't be pre-ticked for anyone"));
            if (rowErrors.Count > 0)
            {
                errors.Add(new(row.Line, string.Join("; ", rowErrors)));
                continue;
            }
            if (row.Room.Length == 0) warnings.Add(new(row.Line, $"{row.Code} section {row.Section}: no room"));
            if (row.Instructor.Length == 0) warnings.Add(new(row.Line, $"{row.Code} section {row.Section}: no instructor"));

            var section = NormalizeSection(row.Section);
            if (!courses.TryGetValue(row.Code, out var entry))
            {
                var name = row.Name.Length > 0 ? row.Name : row.Code;
                entry = (new ParsedCourse(row.Code, name, type!.Value, year, credits, majors, []), new(StringComparer.OrdinalIgnoreCase));
                courses[row.Code] = entry;
            }
            else
            {
                var c = entry.Course;
                if (row.Name.Length > 0 && !string.Equals(c.Name, row.Name, StringComparison.OrdinalIgnoreCase) && c.Name != c.Code)
                    warnings.Add(new(row.Line, $"{row.Code}: name \"{row.Name}\" differs from earlier row (\"{c.Name}\"); using the first"));
                if (c.Type != type) warnings.Add(new(row.Line, $"{row.Code}: type differs from an earlier row; using {c.Type}"));
                foreach (var m in majors.Where(m => !c.Majors.Contains(m, StringComparer.OrdinalIgnoreCase))) c.Majors.Add(m);
            }
            if (!entry.Sections.TryGetValue(section, out var sec))
            {
                sec = new ParsedSection(section, row.Instructor, []);
                entry.Sections[section] = sec;
                entry.Course.Sections.Add(sec);
            }
            else if (row.Instructor.Length > 0 && sec.Instructor.Length > 0 && !string.Equals(sec.Instructor, row.Instructor, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new(row.Line, $"{row.Code} section {section}: two instructors listed; using \"{sec.Instructor}\""));
            }
            else if (sec.Instructor.Length == 0 && row.Instructor.Length > 0)
            {
                var i = entry.Course.Sections.IndexOf(sec);
                sec = sec with { Instructor = row.Instructor };
                entry.Sections[section] = sec;
                entry.Course.Sections[i] = sec;
            }
            foreach (var d in days!)
            {
                var m = new ParsedMeeting(d, time!.Value.Start, time.Value.End, row.Room);
                if (sec.Meetings.Contains(m)) warnings.Add(new(row.Line, $"{row.Code} section {section}: duplicate {Timeslots.DayNames[d]} meeting skipped"));
                else sec.Meetings.Add(m);
            }
        }
        var list = courses.Values.Select(v => v.Course).OrderBy(c => c.Code).ToList();
        return new(list, errors, warnings, rows.Count);
    }

    /// <summary>"01", "Sec 1", "S1" → "1"; anything else is kept as written.</summary>
    public static string NormalizeSection(string raw)
    {
        var t = raw.Trim();
        var lower = t.ToLowerInvariant();
        foreach (var prefix in new[] { "section", "sec.", "sec", "s" })
            if (lower.StartsWith(prefix) && lower.Length > prefix.Length && char.IsAsciiDigit(lower[prefix.Length..].TrimStart()[0]))
            { t = t[prefix.Length..].Trim(); break; }
        return int.TryParse(t, out var n) ? n.ToString() : t.ToUpperInvariant();
    }

    static string MeetingText(IEnumerable<(int Day, TimeOnly Start, TimeOnly End, string Room)> meetings) =>
        string.Join(", ", meetings.OrderBy(m => m.Day).ThenBy(m => m.Start)
            .Select(m => $"{Timeslots.DayNames[m.Day]} {Timeslots.Format(m.Start)}–{Timeslots.Format(m.End)}{(m.Room.Length > 0 ? " " + m.Room : "")}"));

    static string Signature(IEnumerable<(int Day, TimeOnly Start, TimeOnly End, string Room)> meetings) => MeetingText(meetings);

    /// <summary>Human summary of what changed in a section, e.g. "Room A101 → B202; Time Sun 08:00–09:15 → Mon 10:00–11:15".</summary>
    public static string? DescribeChange(string oldInstructor, IEnumerable<Meeting> oldMeetings, string newInstructor, IEnumerable<(int Day, TimeOnly Start, TimeOnly End, string Room)> newMeetings)
    {
        var oldList = oldMeetings.Select(m => (m.Day, m.Start, m.End, m.Room)).ToList();
        var newList = newMeetings.ToList();
        var parts = new List<string>();
        var oldTimes = string.Join(", ", oldList.OrderBy(m => m.Day).ThenBy(m => m.Start).Select(m => $"{Timeslots.DayNames[m.Day]} {Timeslots.Format(m.Start)}–{Timeslots.Format(m.End)}"));
        var newTimes = string.Join(", ", newList.OrderBy(m => m.Day).ThenBy(m => m.Start).Select(m => $"{Timeslots.DayNames[m.Day]} {Timeslots.Format(m.Start)}–{Timeslots.Format(m.End)}"));
        if (oldTimes != newTimes) parts.Add($"Time: {oldTimes} → {newTimes}");
        var oldRooms = string.Join(", ", oldList.Select(m => m.Room).Where(r => r.Length > 0).Distinct());
        var newRooms = string.Join(", ", newList.Select(m => m.Room).Where(r => r.Length > 0).Distinct());
        if (oldRooms != newRooms) parts.Add($"Room: {(oldRooms.Length > 0 ? oldRooms : "none")} → {(newRooms.Length > 0 ? newRooms : "none")}");
        if (!string.Equals(oldInstructor, newInstructor, StringComparison.Ordinal)) parts.Add($"Instructor: {(oldInstructor.Length > 0 ? oldInstructor : "none")} → {(newInstructor.Length > 0 ? newInstructor : "none")}");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    static async Task<List<Course>> LoadSemester(AppDb db, int semesterId, CancellationToken ct) =>
        await db.Courses.Where(c => c.SemesterId == semesterId)
            .Include(c => c.Majors).ThenInclude(cm => cm.Major)
            .Include(c => c.Sections).ThenInclude(s => s.Meetings)
            .AsSplitQuery()
            .ToListAsync(ct);

    static Major? FindMajor(List<Major> majors, string token) =>
        majors.FirstOrDefault(m => string.Equals(m.Code, token, StringComparison.OrdinalIgnoreCase))
        ?? majors.FirstOrDefault(m => string.Equals(m.Name, token, StringComparison.OrdinalIgnoreCase));

    /// <summary>Compares the upload with what the semester holds now, without saving anything.</summary>
    public static async Task<ImportPreview> PreviewAsync(AppDb db, int semesterId, ParsedSchedule parsed, CancellationToken ct)
    {
        var existing = await LoadSemester(db, semesterId, ct);
        var majors = await db.Majors.ToListAsync(ct);
        var byCode = existing.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        var added = new List<Change>();
        var updated = new List<Change>();
        var removed = new List<Change>();
        var affectedSections = new HashSet<int>();
        var affectedCourses = new HashSet<int>();

        var newMajors = parsed.Courses.Where(c => c.Type != CourseType.LS).SelectMany(c => c.Majors)
            .Where(m => FindMajor(majors, m) is null).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m).ToList();

        foreach (var pc in parsed.Courses)
        {
            if (!byCode.TryGetValue(pc.Code, out var course))
            {
                added.Add(new("course", pc.Code, null, $"{pc.Name} · {pc.Type} · {pc.Sections.Count} section{(pc.Sections.Count == 1 ? "" : "s")}"));
                continue;
            }
            var courseDiffs = new List<string>();
            if (course.Name != pc.Name) courseDiffs.Add($"name → {pc.Name}");
            if (course.Type != pc.Type) courseDiffs.Add($"type {course.Type} → {pc.Type}");
            if (course.Year != pc.Year) courseDiffs.Add($"year {course.Year?.ToString() ?? "–"} → {pc.Year?.ToString() ?? "–"}");
            if (course.Credits != pc.Credits) courseDiffs.Add($"credits {course.Credits?.ToString() ?? "–"} → {pc.Credits?.ToString() ?? "–"}");
            var oldMajors = course.Majors.Select(m => m.Major.Code).OrderBy(x => x).ToList();
            var newMajorCodes = pc.Type == CourseType.LS ? [] : pc.Majors.Select(m => FindMajor(majors, m)?.Code ?? m).OrderBy(x => x).ToList();
            if (!oldMajors.SequenceEqual(newMajorCodes, StringComparer.OrdinalIgnoreCase)) courseDiffs.Add($"majors {string.Join("/", oldMajors)} → {string.Join("/", newMajorCodes)}");
            if (courseDiffs.Count > 0) updated.Add(new("course", pc.Code, null, string.Join("; ", courseDiffs)));

            var sections = course.Sections.ToDictionary(s => s.Number, StringComparer.OrdinalIgnoreCase);
            foreach (var ps in pc.Sections)
            {
                if (!sections.TryGetValue(ps.Number, out var sec))
                {
                    added.Add(new("section", pc.Code, ps.Number, MeetingText(ps.Meetings.Select(m => (m.Day, m.Start, m.End, m.Room)))));
                    continue;
                }
                var change = DescribeChange(sec.Instructor, sec.Meetings, ps.Instructor, ps.Meetings.Select(m => (m.Day, m.Start, m.End, m.Room)));
                if (change is not null)
                {
                    updated.Add(new("section", pc.Code, ps.Number, change));
                    affectedSections.Add(sec.Id);
                }
            }
            foreach (var sec in course.Sections.Where(s => !pc.Sections.Any(ps => string.Equals(ps.Number, s.Number, StringComparison.OrdinalIgnoreCase))))
            {
                removed.Add(new("section", pc.Code, sec.Number, MeetingText(sec.Meetings.Select(m => (m.Day, m.Start, m.End, m.Room)))));
                affectedSections.Add(sec.Id);
            }
        }
        foreach (var course in existing.Where(c => !parsed.Courses.Any(pc => string.Equals(pc.Code, c.Code, StringComparison.OrdinalIgnoreCase))))
        {
            removed.Add(new("course", course.Code, null, course.Name));
            affectedCourses.Add(course.Id);
        }

        var studentsAffected = await db.Picks
            .Where(p => (p.SectionId != null && affectedSections.Contains(p.SectionId.Value)) || affectedCourses.Contains(p.CourseId))
            .Select(p => p.StudentId).Distinct().CountAsync(ct);

        return new(parsed.RowCount, parsed.Courses.Count, parsed.Courses.Sum(c => c.Sections.Count),
            parsed.Errors, parsed.Warnings, newMajors, added, updated, removed, studentsAffected,
            CanApply: parsed.Errors.Count == 0 && parsed.Courses.Count > 0);
    }

    /// <summary>
    /// Updates the semester to match the upload: only changed rows are touched, so students keep their picks.
    /// A removed section clears that pick and flags it; a removed course drops its picks.
    /// Changes to a published semester mark the section as "Changed" for students.
    /// </summary>
    public static async Task ApplyAsync(AppDb db, Semester semester, ParsedSchedule parsed, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var majors = await db.Majors.ToListAsync(ct);
        foreach (var token in parsed.Courses.Where(c => c.Type != CourseType.LS).SelectMany(c => c.Majors).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (FindMajor(majors, token) is not null) continue;
            var major = new Major { Code = token.Length <= 8 ? token.ToUpperInvariant() : token, Name = token };
            db.Majors.Add(major);
            majors.Add(major);
        }
        await db.SaveChangesAsync(ct);

        var existing = await LoadSemester(db, semester.Id, ct);
        var byCode = existing.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        var markChanges = semester.IsCurrent;

        foreach (var course in existing.Where(c => !parsed.Courses.Any(pc => string.Equals(pc.Code, c.Code, StringComparison.OrdinalIgnoreCase))))
            db.Courses.Remove(course);

        foreach (var pc in parsed.Courses)
        {
            if (!byCode.TryGetValue(pc.Code, out var course))
            {
                course = new Course { SemesterId = semester.Id, Code = pc.Code };
                db.Courses.Add(course);
            }
            course.Name = pc.Name;
            course.Type = pc.Type;
            course.Year = pc.Year;
            course.Credits = pc.Credits;
            var wanted = pc.Type == CourseType.LS ? [] : pc.Majors.Select(m => FindMajor(majors, m)!).DistinctBy(m => m.Id).ToList();
            course.Majors.RemoveAll(cm => !wanted.Any(w => w.Id == cm.MajorId));
            foreach (var m in wanted.Where(w => !course.Majors.Any(cm => cm.MajorId == w.Id)))
                course.Majors.Add(new CourseMajor { Major = m, MajorId = m.Id });

            foreach (var sec in course.Sections.Where(s => !pc.Sections.Any(ps => string.Equals(ps.Number, s.Number, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                foreach (var pick in await db.Picks.Where(p => p.SectionId == sec.Id).ToListAsync(ct))
                {
                    pick.SectionId = null;
                    pick.Section = null;
                    pick.SectionRemoved = true;
                }
                course.Sections.Remove(sec);
                db.Sections.Remove(sec);
            }
            foreach (var ps in pc.Sections)
            {
                var sec = course.Sections.FirstOrDefault(s => string.Equals(s.Number, ps.Number, StringComparison.OrdinalIgnoreCase));
                var newMeetings = ps.Meetings.Select(m => (m.Day, m.Start, m.End, m.Room)).ToList();
                if (sec is null)
                {
                    course.Sections.Add(new Section
                    {
                        Number = ps.Number,
                        Instructor = ps.Instructor,
                        Meetings = ps.Meetings.Select(m => new Meeting { Day = m.Day, Start = m.Start, End = m.End, Room = m.Room }).ToList(),
                    });
                    continue;
                }
                var change = DescribeChange(sec.Instructor, sec.Meetings, ps.Instructor, newMeetings);
                if (change is null) continue;
                sec.Instructor = ps.Instructor;
                db.Meetings.RemoveRange(sec.Meetings);
                sec.Meetings = ps.Meetings.Select(m => new Meeting { Day = m.Day, Start = m.Start, End = m.End, Room = m.Room }).ToList();
                if (markChanges) { sec.ChangedAt = now; sec.ChangeSummary = change; }
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
