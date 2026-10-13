using UniSchedule.Api.Auth;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Tests;

/// <summary>Year 0 is the foundation year, so the ID's intake year is one year behind the study year.</summary>
public class StudyYearTests
{
    [Theory]
    [InlineData("26-0001", 2026, 0)]
    [InlineData("25-0001", 2026, 1)]
    [InlineData("24-0161", 2026, 2)]
    [InlineData("21-0001", 2026, 5)]
    [InlineData("15-0001", 2026, 5)]
    [InlineData("27-0001", 2026, 0)]
    public void PredictYear_counts_the_foundation_year(string id, int academicStartYear, int expected) =>
        Assert.Equal(expected, StudentIds.PredictYear(id, academicStartYear));

    [Theory]
    [InlineData("Foundation", 0)]
    [InlineData("FY", 0)]
    [InlineData("0", 0)]
    [InlineData("2", 2)]
    [InlineData("Year 3", 3)]
    public void Import_reads_foundation_as_year_0(string cell, int expected)
    {
        var csv = "code,name,major,type,year,section,instructor,days,time,room\n" +
                  $"FY101,Foundation Maths,CS,Core,{cell},1,Dr. A,Sun/Tue,08:00-09:30,B105\n";
        var parsed = ScheduleImport.Parse(ScheduleFile.ReadText(csv).Rows);
        Assert.Equal(expected, Assert.Single(parsed.Courses).Year);
    }
}
