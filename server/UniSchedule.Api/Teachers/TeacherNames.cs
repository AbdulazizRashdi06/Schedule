using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Students;

namespace UniSchedule.Api.Teachers;

/// <summary>
/// Timetable names ("Raed Bolbol" on a section) and how a staff email is matched to one. A link made here needs no
/// approval and the account is then shown to students under that name, so the match is narrow on purpose: only the
/// forms GUtech's own address format produces (firstname.lastname) link by themselves. Everything else picks a name
/// and waits for staff.
/// </summary>
public static partial class TeacherNames
{
    static readonly HashSet<string> Titles = ["dr", "prof", "professor", "mr", "mrs", "ms", "eng"];
    static readonly HashSet<string> NoKeys = [];

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotInKey();

    [GeneratedRegex(@"^[a-z]+\z")]
    private static partial Regex LettersOnly();

    /// <summary>Lower-case with accents dropped: "Müller" is "muller".</summary>
    public static string Fold(string s)
    {
        var folded = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormKD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
                folded.Append(c);
        return folded.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// The only way a section's instructor is compared with a link: "Dr. Fatma Al-Rawahi" is "dr fatma al rawahi", so
    /// case, spacing, punctuation and accents never break one.
    /// </summary>
    public static string NameKey(string? name) => NotInKey().Replace(Fold(name ?? ""), " ").Trim();

    /// <summary>The words of a name, without a leading title: "Dr. Fatma Al-Rawahi" is fatma, al, rawahi.</summary>
    public static List<string> Tokens(string name)
    {
        var tokens = NameKey(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 2 && Titles.Contains(tokens[0])) tokens.RemoveAt(0);
        return tokens;
    }

    /// <summary>
    /// A name that isn't a person ("CS1 CS1", a blank): fewer than two words, all words the same, a digit anywhere, or
    /// no word starting with a capital. Never offered in the picker, never linked, and not tappable on class pages.
    /// </summary>
    public static bool IsPlaceholder(string? name)
    {
        var words = (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length < 2
            || words.All(w => w.Equals(words[0], StringComparison.OrdinalIgnoreCase))
            || words.Any(w => w.Any(char.IsDigit))
            || !words.Any(w => char.IsUpper(w[0]));
    }

    /// <summary>
    /// Whether the part of a staff email before the @ is this name: the exact first name, then a trailing run of the
    /// remaining name words, each email word being one or more whole name words glued together ("alhumairi" is
    /// "al" + "humairi"). No fuzzy matching, no initials, no reversed order, no underscores, no digits.
    /// </summary>
    public static bool Matches(string emailLocalPart, string name)
    {
        if (emailLocalPart.Contains('_')) return false;
        // A dash inside a word is dropped ("al-humairi"); a dash between first and last name leaves one word, so no match.
        var e = emailLocalPart.Split('.').Select(w => w.Replace("-", "")).ToList();
        if (e.Count < 2 || !e.All(LettersOnly().IsMatch)) return false;
        var t = Tokens(name);
        if (t.Count < 2 || !t.All(LettersOnly().IsMatch)) return false;
        if (e[0] != t[0]) return false;
        for (var k = 1; k < t.Count; k++)
            if (Cuts(t, k, e, 1)) return true;
        return false;
    }

    /// <summary>Whether the name words from <paramref name="ti"/> on, glued in runs of whole words, are exactly the email words from <paramref name="ei"/> on.</summary>
    static bool Cuts(List<string> t, int ti, List<string> e, int ei)
    {
        if (ei == e.Count) return ti == t.Count;
        var glued = "";
        for (var j = ti; j < t.Count; j++)
        {
            glued += t[j];
            if (glued.Length > e[ei].Length || !e[ei].StartsWith(glued, StringComparison.Ordinal)) return false;
            if (glued.Length == e[ei].Length && Cuts(t, j + 1, e, ei + 1)) return true;
        }
        return false;
    }

    /// <summary>The names this staff email matches: placeholders left out, one per <see cref="NameKey"/>.</summary>
    public static List<string> Match(string email, IEnumerable<string> names)
    {
        var local = TeacherEmails.LocalPart(email);
        return names.Where(n => !IsPlaceholder(n) && Matches(local, n))
            .GroupBy(NameKey).Select(g => g.OrderBy(n => n, StringComparer.Ordinal).First())
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>The instructors written on the current semester's sections, as written (blanks and placeholders included).</summary>
    public static async Task<List<string>> InstructorsAsync(AppDb db, CancellationToken ct)
    {
        var semester = await StudentEndpoints.CurrentSemester(db, ct);
        return semester is null ? [] : await db.Sections.Where(s => s.Course.SemesterId == semester.Id).Select(s => s.Instructor).ToListAsync(ct);
    }

    /// <summary>The names a teacher can pick as theirs: every real name of the current semester once, with how many sections it has.</summary>
    public static async Task<List<(string Name, int Sections)>> PickableAsync(AppDb db, CancellationToken ct) =>
        (await InstructorsAsync(db, ct)).Where(n => !IsPlaceholder(n))
            .GroupBy(NameKey).Select(g => (Name: g.OrderBy(n => n, StringComparer.Ordinal).First().Trim(), Sections: g.Count()))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Links a staff account to its timetable name when the email matches exactly one. Runs at sign-in and when the
    /// teacher's home loads, only while the account has no link of any kind (so a link staff reversed is never made
    /// again) and a semester is published. Returns whether a link was added.
    /// </summary>
    public static async Task<bool> LinkOnSignInAsync(AppDb db, Teacher teacher, DateTimeOffset now, CancellationToken ct)
    {
        if (teacher.StudentId is not null || teacher.Email is null) return false;
        if (await db.TeacherNames.AnyAsync(n => n.TeacherId == teacher.Id, ct)) return false;
        var matches = Match(teacher.Email, (await InstructorsAsync(db, ct)).Distinct());
        // No name, or more than one: the teacher picks.
        if (matches.Count != 1) return false;
        var name = matches[0].Trim();
        var key = NameKey(name);

        var link = new TeacherName { TeacherId = teacher.Id, Name = name, NameKey = key, State = TeacherName.Auto, How = TeacherName.ByEmail, CreatedAt = now };
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var holder = await db.TeacherNames.FirstOrDefaultAsync(n => n.NameKey == key && (n.State == TeacherName.Auto || n.State == TeacherName.Approved), ct);
            if (holder is not null && holder.How == TeacherName.Picked)
            {
                // The matching email wins over someone who picked the name by hand; staff see both sides.
                holder.State = TeacherName.Pending;
                holder.Conflict = true;
                link.Conflict = true;
                // Saved first: only one account may hold a name at a time.
                await db.SaveChangesAsync(ct);
            }
            else if (holder is not null)
            {
                // Matched by email too, or attached by staff on purpose: the holder keeps it and staff decide.
                link.State = TeacherName.Pending;
                link.Conflict = true;
            }
            db.TeacherNames.Add(link);
            await db.SaveChangesAsync(ct);
            if (link.Holds)
                // Anyone still waiting for this name now waits on a name that is held.
                await db.TeacherNames.Where(n => n.NameKey == key && n.TeacherId != teacher.Id && n.State == TeacherName.Pending)
                    .ExecuteUpdateAsync(u => u.SetProperty(n => n.Conflict, true), ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // Someone else took the name in the same moment. Nothing is linked now; the next load of the home tries again.
            if (tx is not null) await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            db.Attach(teacher);
            return false;
        }
    }

    /// <summary>
    /// Where the account is with its name: linked (holds one), pending (asked for one), pick-name (hasn't chosen yet) or
    /// none ("My name isn't here"). A student's teaching profile is only ever linked or none: owners attach its names.
    /// </summary>
    public static string Status(Teacher teacher, IEnumerable<TeacherName> names)
    {
        var own = names.Where(n => n.TeacherId == teacher.Id).ToList();
        if (own.Any(n => n.Holds)) return "linked";
        if (teacher.StudentId is not null) return "none";
        if (own.Any(n => n.State == TeacherName.Pending)) return "pending";
        return teacher.NameSkippedAt is null ? "pick-name" : "none";
    }

    /// <summary>
    /// The name students see for an account: its oldest held timetable name as written there; else the name an owner
    /// typed (student profiles); else the name in the email ("raed.bolbol" is "Raed Bolbol").
    /// An account that holds no name is never shown under a real teacher's exact name: when the name that would be
    /// shown is in <paramref name="taken"/>, the email's local part is shown as typed instead ("raed_bolbol"), or
    /// "Teaching assistant" for a student profile. So "Cancelled by Raed Bolbol" always means the account linked to that name.
    /// </summary>
    public static string Shown(Teacher teacher, IEnumerable<TeacherName> names, IReadOnlySet<string> taken)
    {
        var held = names.Where(n => n.TeacherId == teacher.Id && n.Holds).OrderBy(n => n.Id).FirstOrDefault();
        if (held is not null) return held.Name;
        var unlinked = teacher.Email is null ? "Teaching assistant" : TeacherEmails.LocalPart(teacher.Email);
        var name = teacher.DisplayName.Trim() is { Length: > 0 } typed ? typed
            : teacher.Email is null ? unlinked : TeacherEmails.DisplayName(teacher.Email);
        return taken.Contains(NameKey(name)) ? unlinked : name;
    }

    /// <summary><see cref="Shown"/>, reading the taken names only when the account holds none.</summary>
    public static async Task<string> ShownAsync(AppDb db, Teacher teacher, IReadOnlyCollection<TeacherName> names, CancellationToken ct) =>
        Shown(teacher, names, names.Any(n => n.TeacherId == teacher.Id && n.Holds) ? NoKeys : await TakenKeysAsync(db, ct));

    /// <summary>The names an unlinked account must not be shown under: the current semester's instructors and every held link.</summary>
    public static async Task<HashSet<string>> TakenKeysAsync(AppDb db, CancellationToken ct)
    {
        var keys = (await InstructorsAsync(db, ct)).Select(NameKey).ToHashSet();
        keys.UnionWith(await db.TeacherNames.Where(n => n.State == TeacherName.Auto || n.State == TeacherName.Approved).Select(n => n.NameKey).ToListAsync(ct));
        keys.Remove("");
        return keys;
    }
}
