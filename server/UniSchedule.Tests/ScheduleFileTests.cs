using ClosedXML.Excel;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Tests;

public class ScheduleFileTests
{
    [Fact]
    public void Reads_csv_with_quoted_fields_and_any_column_order()
    {
        var csv = "Room,Section,Course Code,Course Name,Days,Time,Type,Major,Year\n" +
                  "B105,1,cs 201,\"Data Structures, Part 1\",Sun/Tue,08:00-09:30,Core,CS,2\n";
        var r = ScheduleFile.ReadText(csv);
        Assert.Empty(r.Problems);
        var row = Assert.Single(r.Rows);
        Assert.Equal("CS201", row.Code);
        Assert.Equal("Data Structures, Part 1", row.Name);
        Assert.Equal("B105", row.Room);
        Assert.Equal("Sun/Tue", row.Days);
        Assert.Equal(2, row.Line);
    }

    [Fact]
    public void Reads_tab_separated_text_pasted_from_excel()
    {
        var tsv = "code\tsection\tdays\tstart\tend\nLS101\t2\tMW\t13:30\t15:00\n";
        var r = ScheduleFile.ReadText(tsv);
        var row = Assert.Single(r.Rows);
        Assert.Equal("LS101", row.Code);
        Assert.Equal("13:30 - 15:00", row.Time);
    }

    [Fact]
    public void Reports_missing_required_columns()
    {
        var r = ScheduleFile.ReadText("Course,Name\nCS101,Intro\n");
        Assert.Empty(r.Rows);
        Assert.Contains("section", Assert.Single(r.Problems));
        Assert.Contains("time", r.Problems[0]);
    }

    [Fact]
    public void Reads_xlsx()
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Fall");
        string[][] data =
        [
            ["Course Code", "Section", "Days", "Time", "Room"],
            ["CS101", "1", "Sun/Tue", "8:00 AM - 9:30 AM", "A101"],
        ];
        for (var r = 0; r < data.Length; r++)
            for (var c = 0; c < data[r].Length; c++)
                sheet.Cell(r + 1, c + 1).Value = data[r][c];
        using var ms = new MemoryStream();
        book.SaveAs(ms);
        ms.Position = 0;
        var result = ScheduleFile.Read(ms, "fall.xlsx");
        var row = Assert.Single(result.Rows);
        Assert.Equal("CS101", row.Code);
        Assert.Equal("A101", row.Room);
    }

    [Theory]
    [InlineData("fall-2026.csv")]
    [InlineData("spring-2027.csv")]
    [InlineData("spring-2027.xlsx")]
    public void Sample_files_parse_without_errors(string name)
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "sample-data", name));
        var read = ScheduleFile.Read(stream, name);
        Assert.Empty(read.Problems);
        var parsed = ScheduleImport.Parse(read.Rows);
        Assert.Empty(parsed.Errors);
        Assert.Empty(parsed.Warnings);
        Assert.Contains(parsed.Courses, c => c.Type == CourseType.Core);
        Assert.Contains(parsed.Courses, c => c.Type == CourseType.LS);
        Assert.Contains(parsed.Courses, c => c.Type == CourseType.Elective);
    }

    [Fact]
    public void Parse_groups_rows_into_courses_and_sections()
    {
        var rows = ScheduleFile.ReadText(
            "code,name,major,type,year,section,instructor,days,time,room\n" +
            "CS201,Data Structures,CS,Core,2,01,Dr. A,Sun/Tue,08:00-09:30,B105\n" +
            "CS201,Data Structures,CS,Core,2,Sec 1,Dr. A,Thu,09:45-11:15,Lab L1\n" +
            "CS201,Data Structures,CS,Core,2,2,Dr. B,Mon/Wed,08:00-09:30,B110\n").Rows;
        var parsed = ScheduleImport.Parse(rows);
        Assert.Empty(parsed.Errors);
        var course = Assert.Single(parsed.Courses);
        Assert.Equal(2, course.Sections.Count);
        var s1 = course.Sections.Single(s => s.Number == "1");
        Assert.Equal(3, s1.Meetings.Count);
    }

    [Fact]
    public void Parse_flags_bad_rows_with_line_numbers()
    {
        var rows = ScheduleFile.ReadText(
            "code,type,section,days,time,major\n" +
            "CS201,Core,1,Funday,08:00-09:30,CS\n" +
            "CS202,Optional,1,Sun,08:00-09:30,CS\n" +
            "CS203,Core,1,Sun,08:00-09:30,\n" +
            "LS101,LS,1,Mon,25:00-26:00,\n").Rows;
        var parsed = ScheduleImport.Parse(rows);
        Assert.Equal([2, 3, 4, 5], parsed.Errors.Select(e => e.Line));
        Assert.Contains("days", parsed.Errors[0].Message);
        Assert.Contains("type", parsed.Errors[1].Message);
        Assert.Contains("major", parsed.Errors[2].Message);
        Assert.Contains("time", parsed.Errors[3].Message);
    }

    [Theory]
    [InlineData("01", "1")]
    [InlineData("Sec 2", "2")]
    [InlineData("S3", "3")]
    [InlineData("section 10", "10")]
    [InlineData("l1", "L1")]
    public void NormalizeSection(string raw, string expected) => Assert.Equal(expected, ScheduleImport.NormalizeSection(raw));
}
