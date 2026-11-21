using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using UniSchedule.Api.Admin;
using UniSchedule.Api.Analytics;
using UniSchedule.Api.Data;
using UniSchedule.Api.Planner;
using UniSchedule.Api.Students;

namespace UniSchedule.Tests;

/// <summary>
/// The background workers across restarts and races: the notification worker's reminders, change alerts and semester
/// announcement, the planner AI worker, the usage sampler at midnight and the outside-API fetcher.
/// </summary>
public class WorkerSafetyTests
{
    static readonly TimeSpan Oman = TimeSpan.FromHours(4);
    static DateTimeOffset At(int day, int h, int m, int sec = 0) => new(2026, 10, day, h, m, sec, Oman);
    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    /// <summary>A push service that accepts everything and counts what it was sent.</summary>
    sealed class Pushes(Func<int, Task>? onPost = null) : HttpMessageHandler, IHttpClientFactory
    {
        int posted;
        public int Posted => posted;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref posted);
            if (onPost is not null) await onPost(n);
            ct.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    static NotificationWorker Worker(ApiFactory factory, Pushes pushes) =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new PushSender(pushes, factory.Services.GetRequiredService<IConfiguration>(), NullLogger<PushSender>.Instance),
            TimeProvider.System, NullLogger<NotificationWorker>.Instance);

    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static PushSubscription Device(int studentId, string name)
    {
        using var ec = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var q = ec.ExportParameters(false).Q;
        return new PushSubscription { StudentId = studentId, Endpoint = $"https://fcm.googleapis.com/fcm/send/{name}", P256dh = B64Url([0x04, .. q.X!, .. q.Y!]),
            Auth = B64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)), CreatedAt = DateTimeOffset.UtcNow };
    }

    /// <summary>
    /// A published semester (weeks from Sun 6 Sep 2026) with HX101 every Sunday 08:00 and a student who picked it, wants
    /// reminders 15 minutes before and has one device. Returns the section and the student.
    /// </summary>
    static async Task<(int Section, int Student)> Setup(ApiFactory factory)
    {
        await factory.AddAdmin("owner-worker", "owner-password-1", AdminRole.Owner, mustChange: false);
        var owner = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/auth/admin-login", new { username = "owner-worker", password = "owner-password-1" })).StatusCode);
        var csv = "code,name,major,type,year,section,instructor,date,days,time,room\nHX101,Holiday Things,HXM,Core,1,1,Dr. H,,Sun,08:00-09:30,R1\n";
        var sem = (await Json(await owner.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year = 2026 }))).GetProperty("id").GetInt32();
        using var file = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "c.csv" } };
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/upload/apply", file)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/semesters/{sem}/weeks", new { week1Start = "2026-09-06", lastWeek = 15 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/publish", null)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        // The publish isn't what these tests are about.
        await db.AppSettings.Where(x => x.Key == Notifications.PublishPending).ExecuteDeleteAsync();
        var course = await db.Courses.Include(c => c.Sections).FirstAsync(c => c.SemesterId == sem);
        var student = new Student { UniversityId = "26-9100", CreatedAt = DateTimeOffset.UtcNow, RemindMinutes = 15 };
        db.Students.Add(student);
        await db.SaveChangesAsync();
        db.Picks.Add(new Pick { StudentId = student.Id, CourseId = course.Id, SectionId = course.Sections[0].Id });
        db.PushSubscriptions.Add(Device(student.Id, "worker-1"));
        await db.SaveChangesAsync();
        return (course.Sections[0].Id, student.Id);
    }

    [Fact]
    public async Task A_restart_sends_the_reminders_due_while_it_was_down_once_and_not_stale_ones()
    {
        using var factory = new ApiFactory();
        await Setup(factory);
        var pushes = new Pushes();

        // Sunday 11 Oct, class at 08:00: the 15-minute reminder is due at 07:45. The old process's last round ends at 07:44:30.
        var old = Worker(factory, pushes);
        Assert.Equal(At(11, 7, 44, 30), await old.RoundAsync(At(11, 7, 44), At(11, 7, 44, 30), default));
        Assert.Equal(0, pushes.Posted);

        // It stops; the new one starts at 07:45:10 and picks up at 07:44:30, so the 07:45 reminder still goes out.
        var restarted = Worker(factory, pushes);
        var from = await restarted.RemindersFromAsync(At(11, 7, 45, 10), default);
        Assert.Equal(At(11, 7, 44, 30), from);
        await restarted.RoundAsync(from, At(11, 7, 45, 40), default);
        Assert.Equal(1, pushes.Posted);

        // Another restart doesn't send it again.
        var again = Worker(factory, pushes);
        from = await again.RemindersFromAsync(At(11, 7, 46), default);
        Assert.Equal(At(11, 7, 45, 40), from);
        await again.RoundAsync(from, At(11, 7, 46, 30), default);
        Assert.Equal(1, pushes.Posted);

        // After a long outage it catches up only 15 minutes back.
        Assert.Equal(At(11, 9, 0) - NotificationWorker.CatchUp, await Worker(factory, pushes).RemindersFromAsync(At(11, 9, 0), default));
    }

    [Fact]
    public async Task Catching_up_after_a_restart_skips_reminders_for_classes_that_already_started()
    {
        using var factory = new ApiFactory();
        var (_, student) = await Setup(factory);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDb>().Students.Where(s => s.Id == student).ExecuteUpdateAsync(x => x.SetProperty(s => s.RemindMinutes, 5));
        var pushes = new Pushes();

        // Sunday 11 Oct, class at 08:00, the 5-minute reminder is due at 07:55. The old process's last round ends at 07:54:30.
        await Worker(factory, pushes).RoundAsync(At(11, 7, 54), At(11, 7, 54, 30), default);
        Assert.Equal(0, pushes.Posted);

        // The server is back at 08:09, inside the catch-up window, but the class started at 08:00: no "HX101 in 5 min".
        var late = Worker(factory, pushes);
        var from = await late.RemindersFromAsync(At(11, 8, 9), default);
        Assert.Equal(At(11, 7, 54, 30), from);
        await late.RoundAsync(from, At(11, 8, 9, 30), default);
        Assert.Equal(0, pushes.Posted);

        // Back before the class starts, the missed reminder still goes out (a week later, same set-up).
        await Worker(factory, pushes).RoundAsync(At(18, 7, 54), At(18, 7, 54, 30), default);
        var soon = Worker(factory, pushes);
        from = await soon.RemindersFromAsync(At(18, 7, 58), default);
        await soon.RoundAsync(from, At(18, 7, 58, 30), default);
        Assert.Equal(1, pushes.Posted);
    }

    [Fact]
    public async Task A_change_committed_after_a_round_read_is_still_announced_once()
    {
        using var factory = new ApiFactory();
        var (section, _) = await Setup(factory);
        var pushes = new Pushes();
        var worker = Worker(factory, pushes);
        var mark = At(12, 10, 0);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            db.AppSettings.Add(new AppSetting { Key = Notifications.ChangesSince, Value = mark.ToString("O"), UpdatedBy = "test", UpdatedAt = mark });
            await db.SaveChangesAsync();
        }

        // An upload stamps its change at 10:00:10 (stored times are UTC) but is still committing when the 10:00:20 round reads: it sees nothing.
        await worker.RoundAsync(At(12, 9, 59, 50), At(12, 10, 0, 20), default);
        Assert.Equal(0, pushes.Posted);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await db.Sections.Where(s => s.Id == section).ExecuteUpdateAsync(u => u.SetProperty(s => s.ChangedAt, At(12, 10, 0, 10).ToUniversalTime()).SetProperty(s => s.ChangeSummary, "Room: R1 → R2"));
        }

        // The next round still announces it, and the one after doesn't repeat it.
        await worker.RoundAsync(At(12, 10, 0, 20), At(12, 10, 0, 50), default);
        Assert.Equal(1, pushes.Posted);
        await worker.RoundAsync(At(12, 10, 0, 50), At(12, 10, 1, 20), default);
        Assert.Equal(1, pushes.Posted);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var saved = await db.AppSettings.Where(x => x.Key == Notifications.ChangesSince).Select(x => x.Value).FirstAsync();
            Assert.Equal(At(12, 10, 0, 10), DateTimeOffset.Parse(saved, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Tells the test when the next transaction is about to begin.</summary>
    sealed class Beginning : DbTransactionInterceptor
    {
        public TaskCompletionSource? Next;
        public override ValueTask<InterceptionResult<System.Data.Common.DbTransaction>> TransactionStartingAsync(System.Data.Common.DbConnection connection, TransactionStartingEventData e,
            InterceptionResult<System.Data.Common.DbTransaction> result, CancellationToken ct = default)
        {
            Interlocked.Exchange(ref Next, null)?.TrySetResult();
            return base.TransactionStartingAsync(connection, e, result, ct);
        }
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("edit")]
    public async Task A_change_waiting_for_the_write_lock_is_stamped_after_what_committed_meanwhile(string how)
    {
        using var factory = new ApiFactory();
        var (section, _) = await Setup(factory);
        var beginning = new Beginning();
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.ConfigureDbContext<AppDb>(o => o.AddInterceptors(beginning))));
        var owner = app.CreateClient(new() { HandleCookies = true });
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/auth/admin-login", new { username = "owner-worker", password = "owner-password-1" })).StatusCode);
        int sem;
        string path;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            sem = await db.Sections.Where(s => s.Id == section).Select(s => s.Course.SemesterId).FirstAsync();
            path = db.Database.GetConnectionString()!;
        }

        // Another admin write holds the write lock while an upload or a section edit moving HX101 to R2 starts.
        await using var other = new SqliteConnection(path);
        await other.OpenAsync();
        await using (var begin = other.CreateCommand()) { begin.CommandText = "BEGIN IMMEDIATE"; await begin.ExecuteNonQueryAsync(); }
        var started = beginning.Next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var csv = "code,name,major,type,year,section,instructor,date,days,time,room\nHX101,Holiday Things,HXM,Core,1,1,Dr. H,,Sun,08:00-09:30,R2\n";
        using var file = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "c.csv" } };
        var write = how == "upload" ? owner.PostAsync($"/api/admin/semesters/{sem}/upload/apply", file)
            : owner.PutAsJsonAsync($"/api/admin/sections/{section}", new { instructor = "Dr. H", meetings = new[] { new { day = 0, start = "08:00", end = "09:30", room = "R2" } } });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(50);

        // The other write commits with a later time; a round reading now would move the announced mark up to it.
        var committed = DateTimeOffset.UtcNow;
        await using (var commit = other.CreateCommand()) { commit.CommandText = "COMMIT"; await commit.ExecuteNonQueryAsync(); }
        Assert.Equal(HttpStatusCode.OK, (await write).StatusCode);

        // The waiting change is stamped after it, so it can't land behind that mark and go unannounced.
        using (var scope = factory.Services.CreateScope())
        {
            var changedAt = await scope.ServiceProvider.GetRequiredService<AppDb>().Sections.Where(s => s.Id == section).Select(s => s.ChangedAt).FirstAsync();
            Assert.NotNull(changedAt);
            Assert.True(changedAt > committed, $"stamped {changedAt:O}, the other write committed at {committed:O}");
        }
    }

    /// <summary>Three students with a device each, and a semester waiting to be announced.</summary>
    static async Task Announcing(ApiFactory factory, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var students = Enumerable.Range(1, 3).Select(i => new Student { UniversityId = $"26-92{i:D2}", CreatedAt = DateTimeOffset.UtcNow }).ToList();
        db.Students.AddRange(students);
        await db.SaveChangesAsync();
        db.PushSubscriptions.AddRange(students.Select(s => Device(s.Id, $"out-{s.Id}")));
        await Publish(db, name);
    }

    /// <summary>What publishing does to the pending announcement (AdminEndpoints' publish).</summary>
    static async Task Publish(AppDb db, string name)
    {
        var pending = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == Notifications.PublishPending);
        if (pending is null) db.AppSettings.Add(pending = new AppSetting { Key = Notifications.PublishPending, UpdatedBy = "system" });
        pending.Value = name;
        pending.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_semester_published_while_the_last_one_is_announced_is_announced_next()
    {
        using var factory = new ApiFactory();
        await Announcing(factory, "Spring 2027");
        var pushes = new Pushes(async n =>
        {
            if (n != 1) return;
            // The owner publishes the right semester while "Spring 2027 is out" is going out.
            using var scope = factory.Services.CreateScope();
            await Publish(scope.ServiceProvider.GetRequiredService<AppDb>(), "Fall 2026");
        });
        var worker = Worker(factory, pushes);
        var now = DateTimeOffset.UtcNow;
        await worker.RoundAsync(now.AddSeconds(-30), now, default);
        Assert.Equal(3, pushes.Posted);
        using (var scope = factory.Services.CreateScope())
            Assert.Equal("Fall 2026", await scope.ServiceProvider.GetRequiredService<AppDb>().AppSettings
                .Where(x => x.Key == Notifications.PublishPending).Select(x => x.Value).FirstOrDefaultAsync());

        await worker.RoundAsync(now, now.AddSeconds(30), default);
        Assert.Equal(6, pushes.Posted);
        await worker.RoundAsync(now.AddSeconds(30), now.AddSeconds(60), default);
        Assert.Equal(6, pushes.Posted);
    }

    [Fact]
    public async Task A_stop_part_way_through_the_announcement_does_not_send_it_to_everyone_again()
    {
        using var factory = new ApiFactory();
        await Announcing(factory, "Spring 2027");
        using var stop = new CancellationTokenSource();
        var pushes = new Pushes(n => { if (n == 2) stop.Cancel(); return Task.CompletedTask; });
        var now = DateTimeOffset.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Worker(factory, pushes).RoundAsync(now.AddSeconds(-30), now, stop.Token));
        Assert.Equal(2, pushes.Posted);

        // After the restart nothing is sent again.
        await Worker(factory, pushes).RoundAsync(now, now.AddSeconds(30), default);
        Assert.Equal(2, pushes.Posted);
    }

    /// <summary>An AI gateway that never answers, until the request is cancelled.</summary>
    sealed class Stuck : HttpMessageHandler
    {
        public readonly TaskCompletionSource Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Called.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }
    }

    [Fact]
    public async Task An_AI_request_cut_off_by_a_restart_fails_at_once_and_drops_its_screenshots()
    {
        using var root = new ApiFactory();
        using var handler = new Stuck();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<PlannerAiClient>();
            services.AddSingleton(new PlannerAiClient(new HttpClient(handler), new ConfigurationBuilder().Build()));
        }));
        string jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var semester = new Semester { Name = "AI restart", Term = "Fall", Year = 2026 };
            var admin = new AdminUser { Username = "ai-restart-owner", Role = AdminRole.Owner, MustChangePassword = false };
            db.AddRange(semester, admin);
            await db.SaveChangesAsync();
            var workspace = new PlannerWorkspace { SemesterId = semester.Id, Family = "family:COMP" };
            db.Add(workspace);
            await db.SaveChangesAsync();
            var job = new PlannerAiJob { WorkspaceId = workspace.Id, StudentId = admin.Id, OwnerAccount = true, CreatedAt = DateTimeOffset.UtcNow,
                InputJson = PlannerJson.Write(new PlannerAiInput("Midterm next Thursday", ["data:image/png;base64,aGVsbG8="])) };
            db.PlannerAiJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }
        await handler.Called.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // The server stops mid-inference: the job is failed on the way out, without its screenshots.
        var worker = factory.Services.GetServices<IHostedService>().OfType<PlannerAiWorker>().Single();
        await worker.StopAsync(default);
        using (var scope = factory.Services.CreateScope())
        {
            var job = await scope.ServiceProvider.GetRequiredService<AppDb>().PlannerAiJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
            Assert.Equal("failed", job.Status);
            Assert.Contains("interrupted", job.Error);
            Assert.Empty(PlannerJson.Read<PlannerAiInput>(job.InputJson).Images);
        }

        // One left 'running' anyway (the process was killed) is failed as soon as the next process starts, not 10 minutes later.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await db.PlannerAiJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, "running")
                .SetProperty(j => j.InputJson, PlannerJson.Write(new PlannerAiInput("Midterm next Thursday", ["data:image/png;base64,aGVsbG8="]))));
        }
        var next = new PlannerAiWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<PlannerAiWorker>.Instance);
        await next.StartAsync(default);
        try
        {
            PlannerAiJob? job = null;
            for (var i = 0; i < 50; i++)
            {
                using var scope = factory.Services.CreateScope();
                job = await scope.ServiceProvider.GetRequiredService<AppDb>().PlannerAiJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
                if (job.Status != "running") break;
                await Task.Delay(100);
            }
            Assert.Equal("failed", job!.Status);
            Assert.Equal("{}", job.InputJson);
        }
        finally { await next.StopAsync(default); }
    }

    [Fact]
    public async Task A_sample_just_after_Oman_midnight_counts_only_that_day()
    {
        using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        // Four students last seen at 23:58 on 3 Nov, Oman time (stored times are UTC).
        var lastNight = new DateTimeOffset(2026, 11, 3, 23, 58, 0, Oman).ToUniversalTime();
        db.Students.AddRange(Enumerable.Range(1, 4).Select(i => new Student { UniversityId = $"26-93{i:D2}", CreatedAt = lastNight, LastSeenAt = lastNight }));
        await db.SaveChangesAsync();
        // A request at 23:59:30 is written by the minute timer just after midnight.
        var buffer = new UsageBuffer();
        buffer.Add(new(lastNight.ToUnixTimeSeconds() / 3600 * 3600, "/api/me", "GET", "student", 200), 5, student: true);
        var now = new DateTimeOffset(2026, 11, 4, 0, 0, 20, Oman).ToUniversalTime();
        await UsageAnalytics.Flush(db, buffer, now, default);

        var day = await db.UsageDays.AsNoTracking().SingleAsync(d => d.Day == UsageAnalytics.OmanDay(now));
        Assert.Equal(0, day.ActiveUsers);
        Assert.Equal(0, day.PeakUsers);
    }

    /// <summary>A server that answers headers and part of a body, then either goes quiet or hangs up.</summary>
    static async Task<(Uri Url, TcpListener Listener)> HalfAnswer(bool hangUp)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buf = new byte[4096];
            _ = await stream.ReadAsync(buf);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n[1,2,"));
            await stream.FlushAsync();
            if (hangUp) return;
            await Task.Delay(TimeSpan.FromSeconds(30));
        });
        return (new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"), listener);
    }

    [Fact]
    public async Task An_outside_API_that_stalls_mid_reply_times_out_as_a_connection_problem()
    {
        var (url, listener) = await HalfAnswer(hangUp: false);
        try
        {
            var fetcher = new HttpConnectionFetcher(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }) { Deadline = TimeSpan.FromSeconds(1) };
            var started = DateTime.UtcNow;
            var e = await Assert.ThrowsAsync<ConnectionException>(() => fetcher.GetAsync(url, "", "", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Contains("took longer than 1 seconds", e.Message);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task An_outside_API_that_hangs_up_mid_reply_is_a_connection_problem()
    {
        var (url, listener) = await HalfAnswer(hangUp: true);
        try
        {
            var fetcher = new HttpConnectionFetcher(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
            var e = await Assert.ThrowsAsync<ConnectionException>(() => fetcher.GetAsync(url, "", "", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Contains("cut off", e.Message);
        }
        finally { listener.Stop(); }
    }
}
