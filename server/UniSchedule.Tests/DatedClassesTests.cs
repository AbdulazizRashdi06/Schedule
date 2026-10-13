using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Tests;

/// <summary>Timetables that change week to week: each class has its own date.</summary>
public class DatedClassesTests
{
    [Theory]
    [InlineData("2026-09-21", 2026, 9, 21)]
    [InlineData("21/09/2026", 2026, 9, 21)]
    [InlineData("1/9/2026", 2026, 9, 1)]
    [InlineData("21.09.2026", 2026, 9, 21)]
    [InlineData("Mon(21/09/2026)", 2026, 9, 21)]
    public void ParseDate_reads_common_forms(string raw, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), ScheduleImport.ParseDate(raw));

    [Theory]
    [InlineData("")]
    [InlineData("31/02/2026")]
    [InlineData("next monday")]
    public void ParseDate_rejects_garbage(string raw) => Assert.Null(ScheduleImport.ParseDate(raw));

    static ParsedSchedule Parse(string csv) => ScheduleImport.Parse(ScheduleFile.ReadText(csv).Rows);

    [Fact]
    public void A_date_column_makes_one_class_per_date_and_needs_no_days()
    {
        var p = Parse("code,name,major,type,year,section,instructor,date,time,room\n" +
            "CS 2 114,Databases,CS,Core,2,1,Dr. A,21/09/2026,11:30-13:00,GU1 102\n" +
            "CS 2 114,Databases,CS,Core,2,1,Dr. A,23/09/2026,09:45-11:15,GU1 102\n" +
            "CS 2 114,Databases,CS,Core,2,1,Dr. A,30/09/2026,08:00-09:30,GU2 001\n");
        Assert.Empty(p.Errors);
        var meetings = Assert.Single(Assert.Single(p.Courses).Sections).Meetings;
        Assert.Equal(3, meetings.Count);
        Assert.Equal(new ParsedMeeting(1, new TimeOnly(11, 30), new TimeOnly(13, 0), "GU1 102", new DateOnly(2026, 9, 21)), meetings[0]);
        // 23 Sep 2026 is a Wednesday.
        Assert.Equal(3, meetings[1].Day);
        Assert.Equal("GU2 001", meetings[2].Room);
    }

    [Fact]
    public void A_bad_date_is_an_error()
    {
        var p = Parse("code,section,date,time\nCS1,1,someday,08:00-09:30\n");
        Assert.Contains("can't read date", Assert.Single(p.Errors).Message);
    }

    static Meeting M(int day, string start, string end, DateOnly? date = null) =>
        new() { Day = day, Start = TimeOnly.Parse(start), End = TimeOnly.Parse(end), Date = date };

    [Fact]
    public void Dated_classes_only_clash_on_the_same_date()
    {
        var mon21 = new DateOnly(2026, 9, 21);
        var mon28 = new DateOnly(2026, 9, 28);
        Assert.True(Clashes.Overlap(M(1, "08:00", "09:30", mon21), M(1, "09:00", "10:00", mon21)));
        Assert.False(Clashes.Overlap(M(1, "08:00", "09:30", mon21), M(1, "08:00", "09:30", mon28)));
        // A weekly class meets every Monday, so it clashes with any Monday class at that time.
        Assert.True(Clashes.Overlap(M(1, "08:00", "09:30"), M(1, "08:00", "09:30", mon28)));
    }
}
