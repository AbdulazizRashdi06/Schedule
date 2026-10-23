using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;

namespace UniSchedule.Api.Schedule;

/// <summary>
/// A course whose code ends in "-L" (CS 1 102-L) is the lab of the course without it (CS 1 102). It follows its parent:
/// same type, year and majors, and it's a default wherever the parent is.
/// </summary>
public static class Labs
{
    public const string Suffix = "-L";

    static string Key(string code) => string.Concat(code.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    /// <summary>"CS 1 102-L" → "CS1102"; null when the code isn't a lab.</summary>
    public static string? ParentKey(string code)
    {
        var key = Key(code);
        return key.EndsWith(Suffix, StringComparison.Ordinal) && key.Length > Suffix.Length ? key[..^Suffix.Length] : null;
    }

    /// <summary>Lab course id → parent course id, for the labs of a semester whose parent exists.</summary>
    public static async Task<Dictionary<int, int>> ParentsAsync(AppDb db, int semesterId, CancellationToken ct)
    {
        var courses = await db.Courses.Where(c => c.SemesterId == semesterId).Select(c => new { c.Id, c.Code }).ToListAsync(ct);
        var byKey = courses.GroupBy(c => Key(c.Code)).ToDictionary(g => g.Key, g => g.First().Id);
        var result = new Dictionary<int, int>();
        foreach (var c in courses)
            if (ParentKey(c.Code) is { } p && byKey.TryGetValue(p, out var parent)) result[c.Id] = parent;
        return result;
    }

    /// <summary>The ids plus the labs of any parent among them.</summary>
    public static async Task<List<int>> WithLabsAsync(AppDb db, int semesterId, List<int> ids, CancellationToken ct)
    {
        var parents = await ParentsAsync(db, semesterId, ct);
        var set = ids.ToHashSet();
        return [.. ids, .. parents.Where(p => set.Contains(p.Value) && !set.Contains(p.Key)).Select(p => p.Key)];
    }

    /// <summary>Copies each parent's type, year and majors onto its lab. Call after uploads, syncs and default changes.</summary>
    public static async Task AlignAsync(AppDb db, int semesterId, CancellationToken ct)
    {
        var parents = await ParentsAsync(db, semesterId, ct);
        if (parents.Count == 0) return;
        var ids = parents.Keys.Concat(parents.Values).ToHashSet();
        var courses = await db.Courses.Where(c => ids.Contains(c.Id)).Include(c => c.Majors).ToDictionaryAsync(c => c.Id, ct);
        foreach (var (labId, parentId) in parents)
        {
            var (lab, parent) = (courses[labId], courses[parentId]);
            lab.Type = parent.Type;
            lab.Year = parent.Year;
            var want = parent.Majors.Select(m => m.MajorId).ToHashSet();
            lab.Majors.RemoveAll(m => !want.Contains(m.MajorId));
            foreach (var m in want.Where(m => lab.Majors.All(x => x.MajorId != m)))
                lab.Majors.Add(new CourseMajor { CourseId = lab.Id, MajorId = m });
        }
        await db.SaveChangesAsync(ct);
    }
}
