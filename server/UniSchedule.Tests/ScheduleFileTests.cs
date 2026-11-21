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

    /// <summary>Real Excel date and time cells, stored as numbers with a built-in format, as Excel saves them.</summary>
    [Fact]
    public void Reads_excel_date_and_time_cells_by_value()
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Fall");
        string[] header = ["Course Code", "Section", "Date", "Start", "End", "Major"];
        for (var c = 0; c < header.Length; c++) sheet.Cell(1, c + 1).Value = header[c];
        // 5 Sep and 21 Sep 2026 in the short-date format (14, shown month-first); 8:00 AM and 1:30 PM as h:mm AM/PM (18),
        // h:mm:ss AM/PM (19) and h:mm:ss (21).
        (double Date, int TimeFormat)[] rows = [(46270, 18), (46286, 19), (46286, 21)];
        for (var r = 0; r < rows.Length; r++)
        {
            sheet.Cell(r + 2, 1).Value = "CS101";
            sheet.Cell(r + 2, 2).Value = r + 1;
            sheet.Cell(r + 2, 3).Value = rows[r].Date;
            sheet.Cell(r + 2, 3).Style.NumberFormat.NumberFormatId = 14;
            sheet.Cell(r + 2, 4).Value = 8.0 / 24;
            sheet.Cell(r + 2, 5).Value = 13.5 / 24;
            sheet.Cell(r + 2, 4).Style.NumberFormat.NumberFormatId = rows[r].TimeFormat;
            sheet.Cell(r + 2, 5).Style.NumberFormat.NumberFormatId = rows[r].TimeFormat;
            sheet.Cell(r + 2, 6).Value = "CS";
        }
        using var ms = new MemoryStream();
        book.SaveAs(ms);
        ms.Position = 0;
        var read = ScheduleFile.Read(ms, "fall.xlsx");
        Assert.Equal(["2026-09-05", "2026-09-21", "2026-09-21"], read.Rows.Select(r => r.Date));
        Assert.All(read.Rows, r => Assert.Equal("08:00 - 13:30", r.Time));

        var parsed = ScheduleImport.Parse(read.Rows);
        Assert.Empty(parsed.Errors);
        var meetings = Assert.Single(parsed.Courses).Sections.SelectMany(s => s.Meetings).ToList();
        Assert.Equal([new DateOnly(2026, 9, 5), new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 21)], meetings.Select(m => m.Date));
        Assert.All(meetings, m => Assert.Equal((new TimeOnly(8, 0), new TimeOnly(13, 30)), (m.Start, m.End)));
    }

    [Theory]
    [InlineData("٢٠٢٦-٠٩-٢١")] // Arabic-Indic digits
    [InlineData("２０２６-０９-２１")] // full-width digits
    [InlineData("٢١/٠٩/٢٠٢٦")]
    public void Dates_in_non_ascii_digits_are_unreadable_not_a_crash(string raw)
    {
        Assert.Null(ScheduleImport.ParseDate(raw));
        var parsed = ScheduleImport.Parse(ScheduleFile.ReadText($"code,section,date,time,type\nLS101,1,{raw},08:00-09:30,LS\n").Rows);
        Assert.Contains("can't read date", Assert.Single(parsed.Errors).Message);
    }

    [Fact]
    public void Codes_lose_every_kind_of_space()
    {
        var r = ScheduleFile.ReadText("code,section,days,time,type\n\"CS\u00A0201\",1,Sun,08:00-09:30,LS\n\"cs\t2 01\",2,Mon,08:00-09:30,LS\n");
        Assert.Equal(["CS201", "CS201"], r.Rows.Select(x => x.Code));
        Assert.Single(ScheduleImport.Parse(r.Rows).Courses);
    }

    [Fact]
    public void Row_numbers_count_blank_rows_and_the_rows_above_the_header()
    {
        var csv = ScheduleFile.ReadText("code,section,days,time,major\n\n\nCS202,1,Sun,08:00-09:30,CS\n\nCS201,1,Funday,08:00-09:30,CS\n");
        Assert.Equal([4, 6], csv.Rows.Select(r => r.Line));
        var error = Assert.Single(ScheduleImport.Parse(csv.Rows).Errors);
        Assert.Equal(6, error.Line);
        // The error names the row too.
        Assert.StartsWith("CS201 section 1: ", error.Message);

        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Fall");
        string[][] data = [["code", "section", "days", "time", "major"], ["CS202", "1", "Sun", "08:00-09:30", "CS"], [], ["CS201", "1", "Funday", "08:00-09:30", "CS"]];
        for (var r = 0; r < data.Length; r++)
            for (var c = 0; c < data[r].Length; c++)
                sheet.Cell(r + 3, c + 1).Value = data[r][c];
        using var ms = new MemoryStream();
        book.SaveAs(ms);
        ms.Position = 0;
        // Rows 1 and 2 are empty, so the header is on sheet row 3 and the bad row on row 6.
        var xlsx = ScheduleFile.Read(ms, "fall.xlsx");
        Assert.Equal(6, Assert.Single(ScheduleImport.Parse(xlsx.Rows).Errors).Line);
    }

    [Fact]
    public void Overlapping_meetings_of_a_section_are_kept_with_a_warning()
    {
        var parsed = ScheduleImport.Parse(ScheduleFile.ReadText("code,section,days,time,room,major\n" +
            "CS201,1,Sun,08:00-09:30,R1,CS\nCS201,1,Sun,08:00-09:30,R2,CS\nCS201,1,Mon,08:00-09:30,R1,CS\n").Rows);
        Assert.Equal(3, Assert.Single(parsed.Courses).Sections.Single().Meetings.Count);
        var w = Assert.Single(parsed.Warnings, w => w.Message.Contains("overlap"));
        Assert.Equal(3, w.Line);
        Assert.Equal("CS201 section 1: two meetings overlap on Sun", w.Message);
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
