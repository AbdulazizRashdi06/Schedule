using UniSchedule.Api.Schedule;
using UniSchedule.Api.Teachers;

namespace UniSchedule.Tests;

/// <summary>
/// What a new university timetable does to one teacher's change (ClassChanges.Decide): every row of the table in the
/// design. Pure: no database. A room-only or end-only change by the university never drops a teacher's change.
/// </summary>
public class ClassChangeDecideTests
{
    static readonly DateOnly Sun = new(2026, 11, 29), Mon = new(2026, 11, 30), Tue = new(2026, 12, 1), NextSun = new(2026, 12, 6);
    static readonly SemesterSpan Span = new(new DateOnly(2026, 9, 20), new DateOnly(2026, 12, 26), [], true, 14);

    static ParsedMeeting M(DateOnly date, string start, string end, string room) => new((int)date.DayOfWeek, TimeOnly.Parse(start), TimeOnly.Parse(end), room, date);
    static ParsedMeeting Weekly(DayOfWeek day, string start, string end, string room) => new((int)day, TimeOnly.Parse(start), TimeOnly.Parse(end), room);

    /// <summary>The section as the teacher found it: Sunday and the Sunday after, 08:00 in A101.</summary>
    static readonly List<ParsedMeeting> Old = [M(Sun, "08:00", "09:30", "A101"), M(NextSun, "08:00", "09:30", "A101")];

    static ClassChange Cancel() => new() { Kind = "cancel", OrigDate = Sun, OrigStart = new(8, 0), OrigEnd = new(9, 30), OrigRoom = "A101" };
    static ClassChange Move(string room = "B202") => new()
    {
        Kind = "move", OrigDate = Sun, OrigStart = new(8, 0), OrigEnd = new(9, 30), OrigRoom = "A101",
        NewDate = Tue, NewStart = new(10, 0), NewEnd = new(11, 30), NewRoom = room,
    };
    static ClassChange Room(string room = "C201") => new()
    {
        Kind = "room", OrigDate = Sun, OrigStart = new(8, 0), OrigEnd = new(9, 30), OrigRoom = "A101",
        NewDate = Sun, NewStart = new(8, 0), NewEnd = new(9, 30), NewRoom = room,
    };
    static ClassChange Extra(string room = "B202") => new() { Kind = "extra", NewDate = Tue, NewStart = new(10, 0), NewEnd = new(11, 30), NewRoom = room };

    public static TheoryData<string, ClassChange, List<ParsedMeeting>, Outcome, string?> Table() => new()
    {
        // ---- cancel ----
        { "cancel: the session is still there", Cancel(), Old, Outcome.Keep, null },
        { "cancel: the university only changed the room", Cancel(), [M(Sun, "08:00", "09:30", "B105"), Old[1]], Outcome.Keep, null },
        { "cancel: the university only changed the end", Cancel(), [M(Sun, "08:00", "10:00", "A101"), Old[1]], Outcome.Keep, null },
        { "cancel: the university dropped that session too", Cancel(), [Old[1]], Outcome.Merge, "merged" },
        { "cancel: the session is gone and another date was re-roomed", Cancel(), [M(NextSun, "08:00", "09:30", "B105")], Outcome.Merge, "merged" },
        { "cancel: the session is gone and another date got a new class", Cancel(), [Old[1], M(Mon, "13:15", "14:45", "A101")], Outcome.Merge, "merged" },
        { "cancel: the session reappears at another start that day", Cancel(), [M(Sun, "10:00", "11:30", "A101"), Old[1]], Outcome.Drop, "dropped-moved" },
        // ---- move ----
        { "move: the original session is still there", Move(), Old, Outcome.Keep, null },
        { "move: the university only re-roomed the original", Move(), [M(Sun, "08:00", "09:30", "B105"), Old[1]], Outcome.Keep, null },
        { "move: the university newly lists the new time, same room and end", Move(), [.. Old, M(Tue, "10:00", "11:30", "B202")], Outcome.Keep, null },
        { "move: the university newly lists the new time in another room", Move(), [.. Old, M(Tue, "10:00", "11:30", "B105")], Outcome.Keep, "room-differs" },
        { "move: the university newly lists the new time with another end", Move(), [.. Old, M(Tue, "10:00", "12:00", "B202")], Outcome.Keep, "room-differs" },
        { "move: the university moved it to the same time, room and end", Move(), [M(Tue, "10:00", "11:30", "B 202"), Old[1]], Outcome.Merge, "merged" },
        { "move: the teacher's room was to be announced", Move(""), [M(Tue, "10:00", "11:30", "B105"), Old[1]], Outcome.Merge, "merged" },
        { "move: the university moved it to that time in another room", Move(), [M(Tue, "10:00", "11:30", "B105"), Old[1]], Outcome.MergeTell, "merged" },
        { "move: the university moved it to that time with another end", Move(), [M(Tue, "10:00", "12:00", "B202"), Old[1]], Outcome.MergeTell, "merged" },
        { "move: the university moved it somewhere else", Move(), [M(Mon, "08:00", "09:30", "A101"), Old[1]], Outcome.Drop, "dropped-moved" },
        { "move: the university dropped the session", Move(), [Old[1]], Outcome.Drop, "dropped-moved" },
        // ---- room ----
        { "room: nothing changed", Room(), Old, Outcome.Keep, null },
        { "room: the university changed only the end", Room(), [M(Sun, "08:00", "10:00", "A101"), Old[1]], Outcome.Keep, null },
        { "room: the university now lists the teacher's room", Room(), [M(Sun, "08:00", "09:30", "c 201"), Old[1]], Outcome.Merge, "merged" },
        { "room: the university lists a third room", Room(), [M(Sun, "08:00", "09:30", "B105"), Old[1]], Outcome.KeepTellRoom, "room-differs" },
        { "room: the university moved the session", Room(), [M(Sun, "10:00", "11:30", "A101"), Old[1]], Outcome.Drop, "dropped-moved" },
        { "room: the university dropped the session", Room(), [Old[1]], Outcome.Drop, "dropped-moved" },
        // ---- extra ----
        { "extra: the university lists nothing then", Extra(), Old, Outcome.Keep, null },
        { "extra: the university lists the same class", Extra(), [.. Old, M(Tue, "10:00", "11:30", "B202")], Outcome.Merge, "merged" },
        { "extra: the university lists it in another room", Extra(), [.. Old, M(Tue, "10:00", "11:30", "B105")], Outcome.MergeTell, "merged" },
        { "extra: the university lists it with another end", Extra(), [.. Old, M(Tue, "10:00", "12:00", "B202")], Outcome.MergeTell, "merged" },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Every_row_of_the_table(string what, ClassChange change, List<ParsedMeeting> now, Outcome outcome, string? reason)
    {
        var decided = ClassChanges.Decide(change, Old, now, Span);
        Assert.True(decided == (outcome, reason), $"{what}: {decided}");
    }

    [Fact]
    public void A_move_kept_while_the_university_newly_lists_the_new_time_in_another_room_tells_the_teacher_once()
    {
        List<ParsedMeeting> listed = [.. Old, M(Tue, "10:00", "11:30", "B105")];
        Assert.Equal((Outcome.Keep, "room-differs"), ClassChanges.Decide(Move(), Old, listed, Span));
        // The next import has that session in its old list already: it is news to nobody.
        Assert.Equal((Outcome.Keep, null), ClassChanges.Decide(Move(), listed, listed, Span));
        Assert.Equal((Outcome.Keep, null), ClassChanges.Decide(Move(), listed, [M(Sun, "08:00", "09:30", "A9"), listed[1], listed[2]], Span));
    }

    [Fact]
    public void A_weekly_class_is_matched_on_its_weekday_inside_the_teaching_weeks()
    {
        List<ParsedMeeting> weekly = [Weekly(DayOfWeek.Sunday, "08:00", "09:30", "A101")];
        // Another room every week: not in the key.
        Assert.Equal(Outcome.Keep, ClassChanges.Decide(Cancel(), weekly, [Weekly(DayOfWeek.Sunday, "08:00", "09:30", "B105")], Span).What);
        // Another time that day: the class moved.
        Assert.Equal((Outcome.Drop, "dropped-moved"), ClassChanges.Decide(Cancel(), weekly, [Weekly(DayOfWeek.Sunday, "10:00", "11:30", "A101")], Span));
        Assert.Equal((Outcome.Drop, "dropped-moved"), ClassChanges.Decide(Room(), weekly, [Weekly(DayOfWeek.Monday, "08:00", "09:30", "A101")], Span));
        // The university makes the weekly class what the teacher's move made of one week: the same weekday and time.
        Assert.Equal((Outcome.Merge, "merged"), ClassChanges.Decide(Move(), weekly, [Weekly(DayOfWeek.Tuesday, "10:00", "11:30", "B202")], Span));
        // A day that is off has no session: the change has nothing to be about.
        var off = Span with { DaysOff = new() { [Sun] = "National Day" } };
        Assert.Equal((Outcome.Drop, "dropped-moved"), ClassChanges.Decide(Room(), weekly, weekly, off));
    }

    [Fact]
    public void A_class_listed_in_two_rooms_at_one_time_is_one_session()
    {
        List<ParsedMeeting> two = [M(Sun, "08:00", "09:30", "A101"), M(Sun, "08:00", "09:30", "A102")];
        Assert.Equal(Outcome.Keep, ClassChanges.Decide(Cancel(), two, two, Span).What);
        // A room change to one of the two rooms still replaces both, so it doesn't merge.
        Assert.Equal(Outcome.Keep, ClassChanges.Decide(Room("A102"), two, two, Span).What);
        Assert.Equal((Outcome.Merge, "merged"), ClassChanges.Decide(Room("A102"), two, [two[1]], Span));
    }

    [Fact]
    public void Students_see_the_same_when_the_university_catches_up_with_the_teacher()
    {
        // The university adopts the teacher's move: the move ends, and the timetable shows the same class.
        List<ParsedMeeting> adopted = [M(Tue, "10:00", "11:30", "B202"), Old[1]];
        Assert.True(ClassChanges.SameForStudents(Old, [Move()], adopted, [], Span));
        // In another room they see something else.
        Assert.False(ClassChanges.SameForStudents(Old, [Move()], [M(Tue, "10:00", "11:30", "B105"), Old[1]], [], Span));
        // A cancelled class that the university re-rooms or drops looks the same: it isn't on either way.
        Assert.True(ClassChanges.SameForStudents(Old, [Cancel()], [M(Sun, "08:00", "09:30", "B105"), Old[1]], [Cancel()], Span));
        Assert.True(ClassChanges.SameForStudents(Old, [Cancel()], [Old[1]], [], Span));
        // The teacher's room stays on top of the university's new one.
        Assert.True(ClassChanges.SameForStudents(Old, [Room()], [M(Sun, "08:00", "09:30", "B105"), Old[1]], [Room()], Span));
        // A change to another session of the section is news.
        Assert.False(ClassChanges.SameForStudents(Old, [Cancel()], [Old[0], M(NextSun, "08:00", "09:30", "B105")], [Cancel()], Span));
        // With no change in play, any difference is news.
        Assert.False(ClassChanges.SameForStudents(Old, [], [M(Sun, "08:00", "09:30", "B105"), Old[1]], [], Span));
    }
}
