using UniSchedule.Api.Schedule;

namespace UniSchedule.Tests;

public class TimeslotsTests
{
    [Theory]
    [InlineData("Sun/Tue", new[] { 0, 2 })]
    [InlineData("Mon Wed", new[] { 1, 3 })]
    [InlineData("MW", new[] { 1, 3 })]
    [InlineData("UTR", new[] { 0, 2, 4 })]
    [InlineData("TTh", new[] { 2, 4 })]
    [InlineData("SuTuTh", new[] { 0, 2, 4 })]
    [InlineData("Sunday, Tuesday and Thursday", new[] { 0, 2, 4 })]
    [InlineData("thu", new[] { 4 })]
    [InlineData("Tue/Sun/Tue", new[] { 0, 2 })]
    [InlineData("Sat", new[] { 6 })]
    public void ParseDays_reads_common_forms(string cell, int[] expected) =>
        Assert.Equal(expected, Timeslots.ParseDays(cell));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Xyz")]
    [InlineData("Mon/Funday")]
    public void ParseDays_rejects_garbage(string cell) => Assert.Null(Timeslots.ParseDays(cell));

    [Theory]
    [InlineData("08:00-09:30", "08:00", "09:30")]
    [InlineData("8:00 AM - 9:15 AM", "08:00", "09:15")]
    [InlineData("9:30 - 10:45 AM", "09:30", "10:45")]
    [InlineData("11:30 AM – 1:00 PM", "11:30", "13:00")]
    [InlineData("1:30 PM - 3:00 PM", "13:30", "15:00")]
    [InlineData("2-3:30", "14:00", "15:30")]
    [InlineData("13:30 to 15:00", "13:30", "15:00")]
    [InlineData("12:00-1:00", "12:00", "13:00")]
    public void ParseRange_reads_common_forms(string cell, string start, string end)
    {
        var r = Timeslots.ParseRange(cell);
        Assert.NotNull(r);
        Assert.Equal(start, Timeslots.Format(r.Value.Start));
        Assert.Equal(end, Timeslots.Format(r.Value.End));
    }

    [Fact]
    public void ParseRangeLoose_accepts_military_time()
    {
        var r = Timeslots.ParseRangeLoose("0800-0930");
        Assert.Equal((new TimeOnly(8, 0), new TimeOnly(9, 30)), r);
    }

    [Theory]
    [InlineData("09:30-08:00")]
    [InlineData("8:00")]
    [InlineData("25:00-26:00")]
    [InlineData("8:75-9:00")]
    [InlineData("noon-1")]
    public void ParseRange_rejects_bad_ranges(string cell) => Assert.Null(Timeslots.ParseRangeLoose(cell));
}
