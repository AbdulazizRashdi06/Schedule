using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UniSchedule.Api.Analytics;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

public class UsageAnalyticsTests
{
    static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    static async Task<HttpClient> Student(ApiFactory factory, string id)
    {
        var client = factory.Client();
        var sent = await Json(await client.PostAsJsonAsync("/api/auth/request-code", new { universityId = id }));
        await Json(await client.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code = sent.GetProperty("testCode").GetString() }));
        await Json(await client.GetAsync("/api/me"));
        return client;
    }

    [Fact]
    public async Task Activity_is_durable_aggregated_and_reports_are_owner_only()
    {
        using var factory = new ApiFactory();
        var first = await Student(factory, "25-9701");
        var second = await Student(factory, "25-9702");
        await first.GetAsync("/api/me?private-search=do-not-store");
        await second.GetAsync("/api/me?private-search=another-secret");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => first.GetAsync("/api/me")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().GetAsync("/api/admin/analytics")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await first.GetAsync("/api/admin/analytics")).StatusCode);
        await factory.AddAdmin("analytics-owner", "owner-password-1", AdminRole.Owner, false);
        var owner = factory.Client();
        await Json(await owner.PostAsJsonAsync("/api/auth/admin-login", new { username = "analytics-owner", password = "owner-password-1" }));
        var report = await Json(await owner.GetAsync("/api/admin/analytics?days=1"));
        Assert.Equal(2, report.GetProperty("peak").GetProperty("peakUsers").GetInt32());
        Assert.Equal(2, report.GetProperty("busiestDay").GetProperty("activeUsers").GetInt32());
        Assert.True(report.GetProperty("requests").GetInt64() > 0);
        Assert.DoesNotContain("do-not-store", report.ToString());
        Assert.DoesNotContain("25-9701", report.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/admin/analytics?days=91")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        Assert.Single(await db.UsageDays.ToListAsync());
        Assert.Contains(await db.UsageHours.ToListAsync(), x => x.Route == "/api/me" && x.Requests == 12);
    }

    [Fact]
    public async Task Collection_can_be_disabled()
    {
        using var factory = new ApiFactory();
        factory.Services.GetRequiredService<IConfiguration>()["Analytics:Enabled"] = "false";
        await Student(factory, "25-9703");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        Assert.Empty(await db.UsageDays.ToListAsync());
        Assert.Empty(await db.UsageHours.ToListAsync());
    }

    [Fact]
    public async Task Retention_removes_old_rows_and_keeps_boundary_day()
    {
        using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var now = DateTimeOffset.UtcNow;
        var boundary = UsageAnalytics.OmanDay(now) - 89 * 86400;
        db.UsageDays.AddRange(new UsageDay { Day = boundary - 86400 }, new UsageDay { Day = boundary });
        db.UsageHours.AddRange(new UsageHour { Hour = boundary - 3600 }, new UsageHour { Hour = boundary });
        await db.SaveChangesAsync();
        await UsageAnalytics.Prune(db, now, CancellationToken.None);
        Assert.Equal(boundary, (await db.UsageDays.AsNoTracking().SingleAsync()).Day);
        Assert.Equal(boundary, (await db.UsageHours.AsNoTracking().SingleAsync()).Hour);
    }

    [Fact]
    public void Days_follow_Oman_midnight()
    {
        var before = DateTimeOffset.Parse("2026-11-03T19:59:59Z");
        Assert.Equal(86400, UsageAnalytics.OmanDay(before.AddSeconds(1)) - UsageAnalytics.OmanDay(before));
    }
}
