using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using UniSchedule.Api.Auth;

namespace UniSchedule.Tests;

/// <summary>
/// The teacher guess limits take a try's place before its code is checked, so tries sent together can't overshoot the
/// caps, and give the place back when the try was not a wrong code.
/// </summary>
public class TeacherCodeLimiterTests
{
    sealed class Clock : TimeProvider
    {
        DateTimeOffset now = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() { lock (this) return now; }
        public void Advance(TimeSpan by) { lock (this) now += by; }
    }

    static TeacherCodeLimiter Limiter(Clock clock) => new(clock, new ConfigurationBuilder().Build());

    static HttpContext From(string ip)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return http;
    }

    /// <summary>Many tries at the same moment, each reserving its place before "checking the code".</summary>
    static async Task<List<TeacherCodeLimiter.Slot?>> Together(int count, Func<int, TeacherCodeLimiter.Slot?> reserve)
    {
        using var go = new ManualResetEventSlim();
        var tries = Enumerable.Range(0, count).Select(i => Task.Run(() =>
        {
            go.Wait();
            return reserve(i);
        })).ToList();
        go.Set();
        return [.. await Task.WhenAll(tries)];
    }

    [Fact]
    public async Task Tries_sent_together_cannot_overshoot_the_per_network_cap()
    {
        var limiter = Limiter(new Clock());
        var http = From("203.0.113.9");
        // 200 parallel guesses from one network, at an address that has an account: 30 get to check a code.
        var slots = await Together(200, _ => limiter.Reserve(http, "known@gutech.edu.om", trustedDevice: false, hasAccount: true).Slot);
        Assert.Equal(30, slots.Count(s => s is not null));
        foreach (var s in slots.OfType<TeacherCodeLimiter.Slot>()) limiter.Confirm(s);
        Assert.True(limiter.IsBlocked(http));
        Assert.Equal(TeacherCodeLimiter.Refusal.Network, limiter.Reserve(http, "known@gutech.edu.om", false, true).Refused);
        // Another network is not held back.
        Assert.NotNull(limiter.Reserve(From("203.0.113.10"), "known@gutech.edu.om", false, true).Slot);
    }

    [Fact]
    public async Task Tries_sent_together_cannot_overshoot_the_site_wide_cap_for_new_addresses()
    {
        var limiter = Limiter(new Clock());
        // Each from its own network, each for a made-up address: 40 get through, whatever the timing.
        var slots = await Together(300, i => limiter.Reserve(From($"198.51.{i / 250}.{i % 250 + 1}"), $"made.up{i}@gutech.edu.om", false, hasAccount: false).Slot);
        Assert.Equal(40, slots.Count(s => s is not null));
        // Staff are told once, by the wrong code that is confirmed while the pause is on.
        Assert.Equal(1, slots.OfType<TeacherCodeLimiter.Slot>().Count(limiter.Confirm));
        Assert.NotNull(limiter.NewPausedUntil());
        Assert.Equal(TeacherCodeLimiter.Refusal.NewPaused, limiter.Reserve(From("198.51.100.200"), "another@gutech.edu.om", false, false).Refused);
        // A teacher who already has an account still signs in.
        Assert.NotNull(limiter.Reserve(From("198.51.100.200"), "known@gutech.edu.om", false, true).Slot);
    }

    [Fact]
    public void A_try_that_was_not_a_wrong_code_gives_its_place_back()
    {
        var limiter = Limiter(new Clock());
        var http = From("203.0.113.20");
        // Right codes, expired codes and the like never use up the allowance.
        for (var i = 0; i < 100; i++)
        {
            var (slot, _) = limiter.Reserve(http, $"new{i}@gutech.edu.om", false, false);
            Assert.NotNull(slot);
            limiter.Release(slot);
            // Released twice, or confirmed after a release: nothing more happens.
            limiter.Release(slot);
            Assert.False(limiter.Confirm(slot));
        }
        Assert.False(limiter.IsBlocked(http));
        Assert.Null(limiter.NewPausedUntil());
        // Wrong codes do: 29 confirmed leave one place, and a try still under way holds it.
        for (var i = 0; i < 29; i++) limiter.Confirm(limiter.Reserve(http, "known@gutech.edu.om", false, true).Slot!);
        var last = limiter.Reserve(http, "known@gutech.edu.om", false, true).Slot;
        Assert.NotNull(last);
        Assert.Null(limiter.Reserve(http, "known@gutech.edu.om", false, true).Slot);
        limiter.Release(last);
        Assert.NotNull(limiter.Reserve(http, "known@gutech.edu.om", false, true).Slot);
    }

    [Fact]
    public async Task A_trusted_browser_is_counted_per_email_and_loses_its_trust_at_the_daily_cap()
    {
        var clock = new Clock();
        var limiter = Limiter(clock);
        var http = From("203.0.113.30");
        const string email = "trusted@gutech.edu.om";
        var slots = await Together(100, _ => limiter.Reserve(http, email, trustedDevice: true, hasAccount: true).Slot);
        // 30 as a trusted browser, then it is treated like any other: 30 more on the network's own allowance.
        Assert.Equal(LoginCodes.MaxWrongPerDay, slots.Count(s => s is { Trusted: true }));
        Assert.Equal(30, slots.Count(s => s is { Trusted: false }));
        foreach (var s in slots.OfType<TeacherCodeLimiter.Slot>()) limiter.Confirm(s);
        Assert.True(limiter.TrustSpent(email));
        // The network's hour passes; the email's day has not.
        clock.Advance(TimeSpan.FromHours(2));
        Assert.True(limiter.TrustSpent(email));
        Assert.False(limiter.IsBlocked(http));
        Assert.True(limiter.Reserve(http, email, true, true).Slot is { Trusted: false });
    }
}
