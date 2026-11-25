using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Teachers;
using static UniSchedule.Tests.TeacherTestKit;

namespace UniSchedule.Tests;

/// <summary>Helpers the class-change tests share: the four doors of /api/teach/changes, the clock in Oman time, and what was stored.</summary>
static class ChangeKit
{
    public static readonly TimeSpan Oman = TimeSpan.FromHours(4);

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");
    public static DateTimeOffset OmanTime(DateOnly d, int hour, int minute = 0) => new(d.ToDateTime(new TimeOnly(hour, minute)), Oman);

    /// <summary>Moves the clock to that Oman date and time.</summary>
    public static void GoTo(this TeachWorld w, DateOnly d, int hour, int minute = 0) => w.Time.Set(OmanTime(d, hour, minute));

    /// <summary>
    /// A new browser signed in as that teacher just now. After the clock jumped days ahead, the old browser's code may
    /// be more than 7 days old; this one's is fresh.
    /// </summary>
    public static async Task<HttpClient> Fresh(this TeachWorld w, string email)
    {
        w.Time.Advance(TimeSpan.FromSeconds(61));
        return await w.SignInTeacher(email);
    }

    public static Task<HttpResponseMessage> Preview(this HttpClient c, object body) => c.PostAsJsonAsync("/api/teach/changes/preview", body);
    public static Task<HttpResponseMessage> Change(this HttpClient c, object body) => c.PostAsJsonAsync("/api/teach/changes", body);
    public static Task<HttpResponseMessage> Replace(this HttpClient c, string group, object body) => c.PutAsJsonAsync($"/api/teach/changes/{group}", body);
    public static Task<HttpResponseMessage> Undo(this HttpClient c, string group) => c.PostAsync($"/api/teach/changes/{group}/undo", null);

    /// <summary>The group of the first change in an answer.</summary>
    public static string Group(JsonElement answer) => answer.GetProperty("changes")[0].GetProperty("group").GetString()!;
    public static List<string> Groups(JsonElement answer) =>
        answer.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("group").GetString()!).Distinct().ToList();

    public static async Task<string> Made(this HttpClient c, object body) => Group(await Json(await c.Change(body)));

    /// <summary>Ticks a section on under "Edit my classes".</summary>
    public static async Task AddSection(this HttpClient teacher, int sectionId, bool on = true) =>
        await Json(await teacher.PutAsJsonAsync($"/api/teach/sections/{sectionId}", new { on }));

    /// <summary>Every change ever made, oldest first.</summary>
    public static Task<List<ClassChange>> Rows(this TeachWorld w) => w.Db(db => db.ClassChanges.AsNoTracking().OrderBy(c => c.Id).ToListAsync());
    public static async Task<List<ClassChange>> Live(this TeachWorld w) => (await w.Rows()).Where(c => c.EndedAt is null).ToList();

    public static Task<List<TeachNotice>> Notices(this TeachWorld w, string audience) =>
        w.Db(db => db.TeachNotices.AsNoTracking().Where(n => n.Audience == audience).OrderBy(n => n.Id).ToListAsync());

    /// <summary>What the sending worker does to a notice: claims it, then marks it sent. So students "were told".</summary>
    public static Task MarkSent(this TeachWorld w) => w.Db(db => db.TeachNotices.Where(n => n.SentAt == null)
        .ExecuteUpdateAsync(u => u.SetProperty(n => n.ClaimedAt, w.Time.GetUtcNow()).SetProperty(n => n.SentAt, w.Time.GetUtcNow())));

    public static Task<int> CourseOf(this TeachWorld w, int sectionId) => w.Db(db => db.Sections.Where(s => s.Id == sectionId).Select(s => s.CourseId).SingleAsync());

    /// <summary>One more meeting on a section, as an upload would have listed it: dated, or weekly when <paramref name="date"/> is null.</summary>
    public static Task<int> AddMeeting(this TeachWorld w, int sectionId, DateOnly? date, int day, string start, string end, string room) => w.Db(async db =>
    {
        var section = await db.Sections.Include(s => s.Meetings).SingleAsync(s => s.Id == sectionId);
        var m = new Meeting { Day = date is DateOnly on ? (int)on.DayOfWeek : day, Date = date, Start = TimeOnly.Parse(start), End = TimeOnly.Parse(end), Room = room };
        section.Meetings.Add(m);
        await db.SaveChangesAsync();
        return m.Id;
    });

    public static Task SetSwitch(this TeachWorld w, bool on) => w.Db(async db =>
    {
        var setting = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == UniSchedule.Api.Schedule.ClassChanges.EnabledSetting);
        if (setting is null) db.AppSettings.Add(setting = new AppSetting { Key = UniSchedule.Api.Schedule.ClassChanges.EnabledSetting, UpdatedBy = "test" });
        (setting.Value, setting.UpdatedAt) = (on ? "true" : "false", w.Time.GetUtcNow());
        await db.SaveChangesAsync();
    });

    /// <summary>
    /// The sessions a copy of the app draws from a timetable answer, reading only what the oldest copies know: each
    /// meeting's day, start, end, room, date and excludedDates, and the semester's span and days off.
    /// </summary>
    public static List<(string Code, DateOnly Date, string Start, string End, string Room)> Sessions(JsonElement timetable, DateOnly from, DateOnly to)
    {
        var sessions = new List<(string, DateOnly, string, string, string)>();
        var first = DateOnly.Parse(timetable.GetProperty("span").GetProperty("first").GetString()!);
        var last = DateOnly.Parse(timetable.GetProperty("span").GetProperty("last").GetString()!);
        foreach (var c in timetable.GetProperty("classes").EnumerateArray())
        foreach (var m in c.GetProperty("section").GetProperty("meetings").EnumerateArray())
        {
            var excluded = m.GetProperty("excludedDates").ValueKind == JsonValueKind.Array
                ? m.GetProperty("excludedDates").EnumerateArray().Select(d => d.GetString()!).ToHashSet() : [];
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                var on = m.GetProperty("date").ValueKind == JsonValueKind.String
                    ? m.GetProperty("date").GetString() == Iso(d)
                    : (int)d.DayOfWeek == m.GetProperty("day").GetInt32() && d >= first && d <= last;
                if (on && !excluded.Contains(Iso(d)))
                    sessions.Add((c.GetProperty("code").GetString()!, d, m.GetProperty("start").GetString()!, m.GetProperty("end").GetString()!, m.GetProperty("room").GetString()!));
            }
        }
        return sessions.OrderBy(s => s.Item2).ThenBy(s => s.Item3).ThenBy(s => s.Item1).ToList();
    }
}
