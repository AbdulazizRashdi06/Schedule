using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Students;
using UniSchedule.Api.Teachers;

namespace UniSchedule.Tests;

/// <summary>Keeps every sign-in email instead of sending it. Teacher codes are never in a response, so tests read them here.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    readonly Lock gate = new();
    readonly List<(string Email, string? Code)> sent = [];

    public Task SendSignInCodeAsync(string email, string code, CancellationToken ct)
    {
        lock (gate) sent.Add((email, code));
        return Task.CompletedTask;
    }

    public Task SendCannotSignInAsync(string email, CancellationToken ct)
    {
        lock (gate) sent.Add((email, null));
        return Task.CompletedTask;
    }

    /// <summary>The newest code emailed to this address.</summary>
    public string CodeFor(string address)
    {
        lock (gate)
            return sent.LastOrDefault(m => m.Email == address && m.Code is not null).Code
                ?? throw new InvalidOperationException($"No sign-in code was emailed to {address}.");
    }

    /// <summary>How many codes were emailed to this address.</summary>
    public int Codes(string address)
    {
        lock (gate) return sent.Count(m => m.Email == address && m.Code is not null);
    }

    /// <summary>Whether this address was emailed "This email can't sign in to Classy".</summary>
    public bool CannotSignIn(string address)
    {
        lock (gate) return sent.Any(m => m.Email == address && m.Code is null);
    }
}

/// <summary>Helpers the teacher tests share: signing in by emailed code, and making accounts and timetables directly.</summary>
public static class TeacherTestKit
{
    /// <summary>The answer's JSON; fails the test when the request was refused.</summary>
    public static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<string> Error(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString()!;

    public static string WrongFor(string code) => code == "000000" ? "111111" : "000000";

    /// <summary>Asks for a teacher code and reads it from the captured email.</summary>
    public static async Task<string> RequestTeacherCode(HttpClient c, CapturingEmailSender emails, string email)
    {
        var r = await c.PostAsJsonAsync("/api/auth/teacher/request-code", new { email });
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return emails.CodeFor(email);
    }

    public static Task<HttpResponseMessage> VerifyTeacher(HttpClient c, string email, string code, string? endpoint = null) =>
        c.PostAsJsonAsync("/api/auth/teacher/verify", new { email, code, endpoint });

    /// <summary>Signs this browser in as a teacher: request, read the captured code, verify. Returns verify's answer.</summary>
    public static async Task<JsonElement> SignInTeacher(HttpClient c, CapturingEmailSender emails, string email) =>
        await Json(await VerifyTeacher(c, email, await RequestTeacherCode(c, emails, email)));

    /// <summary>A new browser signed in as that teacher.</summary>
    public static async Task<HttpClient> SignInTeacher(ApiFactory factory, string email)
    {
        var c = factory.Client();
        await SignInTeacher(c, factory.Emails, email);
        return c;
    }

    /// <summary>Signs this browser in as a student, with the emailed code (owners and teaching assistants get none on screen).</summary>
    public static async Task<JsonElement> SignInStudent(HttpClient c, CapturingEmailSender emails, string universityId)
    {
        var r = await c.PostAsJsonAsync("/api/auth/request-code", new { universityId });
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await Json(await c.PostAsJsonAsync("/api/auth/verify", new { universityId, code = emails.CodeFor(StudentIds.EmailFor(universityId)) }));
    }

    public static async Task<HttpClient> SignInStudent(ApiFactory factory, string universityId)
    {
        var c = factory.Client();
        await SignInStudent(c, factory.Emails, universityId);
        return c;
    }

    public static async Task<HttpClient> SignInAdmin(HttpClient c, string username, string password)
    {
        var r = await c.PostAsJsonAsync("/api/auth/admin-login", new { username, password });
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return c;
    }

    public static async Task<T> Db<T>(this IServiceProvider services, Func<AppDb, Task<T>> f)
    {
        using var scope = services.CreateScope();
        return await f(scope.ServiceProvider.GetRequiredService<AppDb>());
    }

    public static async Task Db(this IServiceProvider services, Func<AppDb, Task> f)
    {
        using var scope = services.CreateScope();
        await f(scope.ServiceProvider.GetRequiredService<AppDb>());
    }

    /// <summary>A staff account that never signed in, holding these timetable names (attached by staff, so approved).</summary>
    public static Task<Teacher> AddTeacher(this IServiceProvider services, string email, params string[] names) => services.Db(async db =>
    {
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var teacher = new Teacher { Email = email, CreatedAt = now };
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();
        db.TeacherNames.AddRange(names.Select(n => new TeacherName
        {
            TeacherId = teacher.Id, Name = n, NameKey = TeacherNames.NameKey(n), State = TeacherName.Approved, How = TeacherName.ByStaff,
            CreatedAt = now, DecidedAt = now, DecidedBy = "test",
        }));
        await db.SaveChangesAsync();
        return teacher;
    });

    /// <summary>Gives a student account teaching rights, as an owner does: a teaching profile with the name the owner typed.</summary>
    public static Task<Teacher> AddTeachingStudent(this IServiceProvider services, int studentId, string name) => services.Db(async db =>
    {
        var teacher = new Teacher { StudentId = studentId, DisplayName = name, CreatedAt = services.GetRequiredService<TimeProvider>().GetUtcNow(), CreatedBy = "test" };
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();
        return teacher;
    });

    /// <summary>A published current semester with one course and one weekly section per instructor given. Returns its id.</summary>
    public static Task<int> AddSemester(this IServiceProvider services, params string[] instructors) => services.Db(async db =>
    {
        var semester = new Semester { Name = "Fall 2026", Term = "Fall", Year = 2026, IsCurrent = true, PublishedAt = DateTimeOffset.UtcNow, Week1Start = new(2026, 9, 20), LastWeek = 18 };
        db.Semesters.Add(semester);
        await db.SaveChangesAsync();
        db.Courses.Add(new Course { SemesterId = semester.Id, Code = "TK101", Name = "Test course", Type = CourseType.Core, Year = 1 });
        await db.SaveChangesAsync();
        foreach (var instructor in instructors) await AddSectionAsync(db, instructor);
        return semester.Id;
    });

    /// <summary>One more section under that instructor in the current semester's test course.</summary>
    public static Task AddSection(this IServiceProvider services, string instructor) => services.Db(db => AddSectionAsync(db, instructor));

    static async Task AddSectionAsync(AppDb db, string instructor)
    {
        var course = await db.Courses.Include(c => c.Sections).FirstAsync(c => c.Semester.IsCurrent && c.Code == "TK101");
        course.Sections.Add(new Section
        {
            Number = (course.Sections.Count + 1).ToString(), Instructor = instructor,
            Meetings = [new() { Day = course.Sections.Count % 5, Start = new(8, 0), End = new(9, 30), Room = "T" + (course.Sections.Count + 1) }],
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// The API with a clock the test moves, the captured emails and a push service that accepts everything, plus (unless
/// <c>seed</c> is off) a small university: a published semester of dated classes, a dashboard owner and RSA, two
/// teachers linked to their names, and three students with picks.
/// </summary>
sealed class TeachWorld : IDisposable
{
    /// <summary>Starts at the real time (cookies carry real expiry dates) and only moves forward.</summary>
    public sealed class Clock : TimeProvider
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        public override DateTimeOffset GetUtcNow() { lock (this) return now; }
        public void Advance(TimeSpan by) { lock (this) now += by; }
        public void Set(DateTimeOffset to)
        {
            lock (this)
            {
                if (to < now) throw new InvalidOperationException("The test clock only moves forward.");
                now = to.ToUniversalTime();
            }
        }
    }

    /// <summary>A push service that accepts everything and keeps the device addresses it was sent to.</summary>
    public sealed class FakePush : HttpMessageHandler
    {
        readonly List<string> sent = [];
        public IReadOnlyList<string> Sent { get { lock (sent) return [.. sent]; } }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (sent) sent.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }
    }

    public const string OwnerName = "teach-owner", RsaName = "teach-rsa", Password = "teach-password-1";
    public const string Teacher1Email = "salim.alharthy@gutech.edu.om", Teacher1Name = "Salim Al Harthy";
    public const string Teacher2Email = "huda.alkindi@gutech.edu.om", Teacher2Name = "Dr. Huda Al-Kindi";
    /// <summary>A name on the timetable that nobody has claimed, a placeholder account, and the three students.</summary>
    public const string UnclaimedName = "Maryam Nasser", PlaceholderName = "CS1 CS1";
    public static readonly string[] StudentIdsList = ["26-7001", "26-7002", "26-7003"];
    static readonly TimeSpan Oman = TimeSpan.FromHours(4);

    readonly ApiFactory root = new();
    public readonly WebApplicationFactory<Program> App;
    public readonly Clock Time = new();
    public readonly FakePush Pushes = new();
    public CapturingEmailSender Emails => root.Emails;
    public IServiceProvider Services => App.Services;

    public HttpClient Owner = null!, Rsa = null!, Teacher1 = null!, Teacher2 = null!;
    public HttpClient[] Students = [];
    public int Semester, Teacher1Id, Teacher2Id;
    public int[] StudentRowIds = [];
    /// <summary>The Sunday the dated classes start on: the first one after today (Oman).</summary>
    public DateOnly FirstSunday;

    TeachWorld(Action<IWebHostBuilder>? configure)
    {
        App = root.WithWebHostBuilder(b =>
        {
            configure?.Invoke(b);
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<TimeProvider>(Time);
                s.AddHttpClient("push").ConfigurePrimaryHttpMessageHandler(() => Pushes);
            });
        });
    }

    public static async Task<TeachWorld> Create(bool seed = true, Action<IWebHostBuilder>? configure = null)
    {
        var w = new TeachWorld(configure);
        try
        {
            if (seed) await w.Seed();
            return w;
        }
        catch
        {
            w.Dispose();
            throw;
        }
    }

    public DateOnly OmanToday => DateOnly.FromDateTime(Time.GetUtcNow().ToOffset(Oman).DateTime);
    /// <summary>A teaching date: <paramref name="week"/> 0 is the week of <see cref="FirstSunday"/>, <paramref name="day"/> 0 is Sunday.</summary>
    public DateOnly Date(int week, int day) => FirstSunday.AddDays(week * 7 + day);

    /// <summary>A browser of its own (own cookies), seen as coming from <paramref name="ip"/>.</summary>
    public HttpClient Browser(string? ip = null)
    {
        var c = App.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        if (ip is not null) c.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
        return c;
    }

    public async Task<HttpClient> SignInTeacher(string email, string? ip = null)
    {
        var c = Browser(ip);
        await TeacherTestKit.SignInTeacher(c, Emails, email);
        return c;
    }

    public async Task<HttpClient> SignInStudent(string universityId, string? ip = null)
    {
        var c = Browser(ip);
        await TeacherTestKit.SignInStudent(c, Emails, universityId);
        return c;
    }

    /// <summary>The owner who is a student (listed in Owners:Ids).</summary>
    public Task<HttpClient> SignInStudentOwner() => SignInStudent(ApiFactory.OwnerId);

    /// <summary>A browser that sends the "view as" header for this teacher key on every request.</summary>
    public static HttpClient ViewAs(HttpClient c, string key)
    {
        c.DefaultRequestHeaders.Remove(TeachActor.ViewAsHeader);
        c.DefaultRequestHeaders.Add(TeachActor.ViewAsHeader, key);
        return c;
    }

    public Task<T> Db<T>(Func<AppDb, Task<T>> f) => Services.Db(f);
    public Task Db(Func<AppDb, Task> f) => Services.Db(f);

    public async Task AddAdmin(string username, AdminRole role)
    {
        await Db(async db =>
        {
            var user = new AdminUser { Username = username, Role = role, MustChangePassword = false, CreatedAt = Time.GetUtcNow() };
            user.PasswordHash = AuthEndpoints.HashPassword(user, Password);
            db.AdminUsers.Add(user);
            await db.SaveChangesAsync();
        });
    }

    public Task<int> Section(string code, string number) =>
        Db(db => db.Sections.Where(s => s.Course.SemesterId == Semester && s.Course.Code == code && s.Number == number).Select(s => s.Id).SingleAsync());

    async Task Seed()
    {
        await AddAdmin(OwnerName, AdminRole.Owner);
        await AddAdmin(RsaName, AdminRole.Rsa);
        Owner = await TeacherTestKit.SignInAdmin(Browser(), OwnerName, Password);
        Rsa = await TeacherTestKit.SignInAdmin(Browser(), RsaName, Password);

        // Dated classes in the two weeks from the next Sunday; the teaching weeks start four weeks before it.
        FirstSunday = OmanToday.AddDays(1);
        while (FirstSunday.DayOfWeek != DayOfWeek.Sunday) FirstSunday = FirstSunday.AddDays(1);
        string D(int week, int day) => Date(week, day).ToString("dd/MM/yyyy");
        var csv = new StringBuilder("code,name,major,type,year,section,instructor,date,time,room\n");
        foreach (var week in new[] { 0, 1 })
        {
            // One real class under three course codes: same teacher, time and room.
            foreach (var code in new[] { "BE1101", "IBSM1101", "LOG1101" })
                csv.Append($"{code},Business Economics,{code[..^4]},Core,1,1,{Teacher1Name},{D(week, 0)},08:00-09:30,A101\n");
            csv.Append($"BE1101,Business Economics,BE,Core,1,2,{UnclaimedName},{D(week, 1)},09:45-11:15,A102\n");
            // Two sections of one course under the other teacher.
            csv.Append($"CS2201,Data Structures,CS,Core,2,1,{Teacher2Name},{D(week, 1)},08:00-09:30,B105\n");
            csv.Append($"CS2201,Data Structures,CS,Core,2,2,{Teacher2Name},{D(week, 2)},08:00-09:30,B105\n");
            csv.Append($"CS2201-L,Data Structures Lab,CS,Core,2,1,{PlaceholderName},{D(week, 2)},11:30-13:00,Lab L2\n");
            csv.Append($"LS1001,Academic English,,LS,,1,,{D(week, 2)},13:15-14:45,Online\n");
        }
        Semester = (await TeacherTestKit.Json(await Owner.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year = 2026 }))).GetProperty("id").GetInt32();
        using (var file = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv.ToString())), "file", "teach.csv" } })
            await TeacherTestKit.Json(await Owner.PostAsync($"/api/admin/semesters/{Semester}/upload/apply", file));
        await TeacherTestKit.Json(await Owner.PutAsJsonAsync($"/api/admin/semesters/{Semester}/weeks", new { week1Start = FirstSunday.AddDays(-28).ToString("yyyy-MM-dd"), lastWeek = 12 }));
        await TeacherTestKit.Json(await Owner.PostAsync($"/api/admin/semesters/{Semester}/publish", null));
        // The "new semester" announcement isn't what these tests are about.
        await Db(db => db.AppSettings.Where(x => x.Key == Notifications.PublishPending).ExecuteDeleteAsync());

        // Students first, so student 1 and teacher 1 share an id and a mix-up between the two tables would show.
        Students = new HttpClient[StudentIdsList.Length];
        for (var i = 0; i < StudentIdsList.Length; i++) Students[i] = await SignInStudent(StudentIdsList[i]);
        var picks = new (string Code, string Number)[][]
        {
            [("BE1101", "1"), ("CS2201", "1")],
            [("IBSM1101", "1"), ("CS2201", "1")],
            [("LOG1101", "1"), ("CS2201", "2")],
        };
        StudentRowIds = await Db(async db =>
        {
            var major = await db.Majors.FirstAsync(m => m.Code == "CS");
            var ids = new int[StudentIdsList.Length];
            for (var i = 0; i < StudentIdsList.Length; i++)
            {
                var s = await db.Students.FirstAsync(x => x.UniversityId == StudentIdsList[i]);
                s.MajorId = major.Id; s.Year = 1; s.SetupSemesterId = Semester; s.SetupStep = "done"; s.SetupCompletedAt = Time.GetUtcNow();
                foreach (var (code, number) in picks[i])
                {
                    var section = await db.Sections.FirstAsync(x => x.Course.SemesterId == Semester && x.Course.Code == code && x.Number == number);
                    db.Picks.Add(new Pick { StudentId = s.Id, CourseId = section.CourseId, SectionId = section.Id });
                }
                ids[i] = s.Id;
            }
            await db.SaveChangesAsync();
            return ids;
        });

        Teacher1 = await SignInTeacher(Teacher1Email);
        Teacher2 = await SignInTeacher(Teacher2Email);
        Teacher1Id = await Db(db => db.Teachers.Where(t => t.Email == Teacher1Email).Select(t => t.Id).SingleAsync());
        Teacher2Id = await Db(db => db.Teachers.Where(t => t.Email == Teacher2Email).Select(t => t.Id).SingleAsync());
    }

    /// <summary>Nothing points at a row that is gone: no foreign key is broken, and every live class change still has its section.</summary>
    public async Task AssertNoOrphans()
    {
        await Db(async db =>
        {
            await db.Database.OpenConnectionAsync();
            await using var check = db.Database.GetDbConnection().CreateCommand();
            check.CommandText = "PRAGMA foreign_key_check";
            await using (var broken = await check.ExecuteReaderAsync())
                Assert.False(await broken.ReadAsync(), "A foreign key points at a missing row.");
            Assert.Empty(await db.ClassChanges.Where(c => c.EndedAt == null && c.SectionId == null).Select(c => c.Id).ToListAsync());
        });
    }

    public void Dispose()
    {
        App.Dispose();
        root.Dispose();
    }
}
