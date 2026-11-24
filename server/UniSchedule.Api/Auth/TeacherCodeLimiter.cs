namespace UniSchedule.Api.Auth;

/// <summary>
/// The guess limits only teacher sign-in has. A made-up staff address is a new key every time, and a hit gives an
/// account that can change any class, so on top of the per-address limits every code has (see <see cref="LoginCodes"/>)
/// wrong teacher codes are counted three more ways, all in memory:
/// per client IP from browsers the address hasn't signed in on (RateLimits:TeacherWrongCodesPerHour, default 30 an hour);
/// site-wide for addresses that have no account yet (RateLimits:TeacherWrongCodesNewPerHour, default 40 an hour), which
/// pauses first-time sign-ins until the count falls; and per address from browsers it has signed in on (30 a day),
/// after which those browsers stop counting as trusted for it.
/// </summary>
public sealed class TeacherCodeLimiter(TimeProvider clock, IConfiguration config)
{
    static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    const int MaxKeys = 50_000;

    readonly int perIp = config.GetValue("RateLimits:TeacherWrongCodesPerHour", 30);
    readonly int forNew = config.GetValue("RateLimits:TeacherWrongCodesNewPerHour", 40);
    readonly Lock gate = new();
    readonly Dictionary<string, Queue<DateTimeOffset>> byIp = [];
    readonly Dictionary<string, Queue<DateTimeOffset>> byEmail = [];
    readonly Queue<DateTimeOffset> newAddresses = new();
    DateTimeOffset? lastAlert;

    /// <summary>This address has used up its wrong teacher codes for the hour (browsers the email signed in on aren't held to it).</summary>
    public bool IsBlocked(HttpContext http)
    {
        var now = clock.GetUtcNow();
        lock (gate) return Count(byIp, ClientAddress.Key(http), now - Hour) >= perIp;
    }

    /// <summary>When the pause on first-time teacher sign-ins ends, or null while there is none.</summary>
    public DateTimeOffset? NewPausedUntil()
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            Trim(newAddresses, now - Hour);
            // It ends when enough of the oldest wrong codes are an hour old to bring the count under the limit.
            return newAddresses.Count >= forNew ? newAddresses.ElementAt(newAddresses.Count - forNew) + Hour : null;
        }
    }

    /// <summary>Browsers this email signed in on have used up their wrong codes for the day, so they are treated like any other browser.</summary>
    public bool TrustSpent(string email)
    {
        var now = clock.GetUtcNow();
        lock (gate) return Count(byEmail, email, now - LoginCodes.Day) >= LoginCodes.MaxWrongPerDay;
    }

    /// <summary>
    /// Counts a wrong teacher code: per IP when the browser wasn't trusted, per email when it was, and site-wide when
    /// the email has no account. Returns true when this one started the pause on first-time sign-ins and staff
    /// haven't been told about one in the last hour.
    /// </summary>
    public bool Record(HttpContext http, string email, bool trusted, bool hasAccount)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (trusted) Add(byEmail, email, now, now - LoginCodes.Day, LoginCodes.MaxWrongPerDay);
            else Add(byIp, ClientAddress.Key(http), now, now - Hour, perIp);
            if (hasAccount) return false;
            Trim(newAddresses, now - Hour);
            var before = newAddresses.Count;
            newAddresses.Enqueue(now);
            // Never more than a few past the limit (tries already under way when the pause began), but keep it bounded.
            while (newAddresses.Count > forNew * 2) newAddresses.Dequeue();
            if (before >= forNew || newAddresses.Count < forNew) return false;
            if (lastAlert is DateTimeOffset told && now - told < Hour) return false;
            lastAlert = now;
            return true;
        }
    }

    static void Trim(Queue<DateTimeOffset> times, DateTimeOffset cutoff)
    {
        while (times.TryPeek(out var oldest) && oldest <= cutoff) times.Dequeue();
    }

    static int Count(Dictionary<string, Queue<DateTimeOffset>> table, string key, DateTimeOffset cutoff)
    {
        if (!table.TryGetValue(key, out var times)) return 0;
        Trim(times, cutoff);
        if (times.Count == 0) table.Remove(key);
        return times.Count;
    }

    static void Add(Dictionary<string, Queue<DateTimeOffset>> table, string key, DateTimeOffset now, DateTimeOffset cutoff, int limit)
    {
        if (!table.TryGetValue(key, out var times))
        {
            if (table.Count >= MaxKeys)
            {
                // Drop keys with nothing left in the window; if it is still full, stop tracking new ones rather than grow without end.
                foreach (var (k, q) in table)
                {
                    Trim(q, cutoff);
                    if (q.Count == 0) table.Remove(k);
                }
                if (table.Count >= MaxKeys) return;
            }
            table[key] = times = new Queue<DateTimeOffset>();
        }
        Trim(times, cutoff);
        times.Enqueue(now);
        // Once a key is at its limit nothing more is counted for it, so each key holds about that many times at most.
        while (times.Count > limit * 2) times.Dequeue();
    }
}
