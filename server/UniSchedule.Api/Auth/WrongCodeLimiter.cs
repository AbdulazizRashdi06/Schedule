namespace UniSchedule.Api.Auth;

/// <summary>
/// Wrong sign-in codes per client IP per rolling hour (RateLimits:WrongCodesPerHour, default 300), so one address
/// can't spray guesses across many IDs. High because everyone on campus Wi-Fi shares one IP, and a browser the ID
/// has signed in on before isn't held to it (or counted), so a neighbour can't keep returning students out. In memory: each address
/// keeps two counts (this hour and the last), and addresses quiet for two hours are dropped.
/// </summary>
public sealed class WrongCodeLimiter(TimeProvider clock, IConfiguration config)
{
    const int MaxAddresses = 50_000;
    readonly int perHour = config.GetValue("RateLimits:WrongCodesPerHour", 300);
    readonly Dictionary<string, (long Hour, int This, int Last)> counts = [];
    long lastPurge;

    public bool IsBlocked(HttpContext http)
    {
        var now = clock.GetUtcNow();
        lock (counts) return counts.TryGetValue(ClientAddress.Key(http), out var c) && Estimate(c, now) >= perHour;
    }

    public void Record(HttpContext http)
    {
        var now = clock.GetUtcNow();
        var hour = Hour(now);
        var key = ClientAddress.Key(http);
        lock (counts)
        {
            if (hour != lastPurge)
            {
                lastPurge = hour;
                foreach (var (k, c) in counts)
                    if (c.Hour < hour - 1) counts.Remove(k);
            }
            if (!counts.TryGetValue(key, out var e))
            {
                // Full of addresses all active in the last two hours: someone with that many has other ways round a
                // per-address limit anyway, so stop tracking new ones rather than grow without end.
                if (counts.Count >= MaxAddresses) return;
                e = (hour, 0, 0);
            }
            e = e.Hour == hour ? e : (hour, 0, e.Hour == hour - 1 ? e.This : 0);
            counts[key] = (hour, e.This + 1, e.Last);
        }
    }

    /// <summary>The last hour's count, weighted by how much of it still falls in the rolling hour, plus this hour's.</summary>
    static double Estimate((long Hour, int This, int Last) c, DateTimeOffset now)
    {
        var hour = Hour(now);
        var into = (now.ToUnixTimeSeconds() % 3600) / 3600.0;
        if (c.Hour == hour) return c.This + c.Last * (1 - into);
        if (c.Hour == hour - 1) return c.This * (1 - into);
        return 0;
    }

    static long Hour(DateTimeOffset now) => now.ToUnixTimeSeconds() / 3600;
}
