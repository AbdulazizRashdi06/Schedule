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
    readonly Dictionary<string, List<DateTimeOffset>> byIp = [];
    readonly Dictionary<string, List<DateTimeOffset>> byEmail = [];
    readonly List<DateTimeOffset> newAddresses = [];
    /// <summary>Tries for new addresses that were let through and whose code is still being checked.</summary>
    int checkingNew;
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
            return newAddresses.Count >= forNew ? newAddresses[^forNew] + Hour : null;
        }
    }

    /// <summary>Browsers this email signed in on have used up their wrong codes for the day, so they are treated like any other browser.</summary>
    public bool TrustSpent(string email)
    {
        var now = clock.GetUtcNow();
        lock (gate) return Count(byEmail, email, now - LoginCodes.Day) >= LoginCodes.MaxWrongPerDay;
    }

    /// <summary>A try that was let through and counted before its code was checked (<see cref="Reserve"/>).</summary>
    public sealed class Slot
    {
        internal string? Ip, Email;
        internal bool New, Settled;
        internal DateTimeOffset At;
        /// <summary>Whether this try counts as coming from a browser the email signed in on.</summary>
        public bool Trusted { get; internal init; }
    }

    public enum Refusal { None, Network, NewPaused }

    /// <summary>
    /// Lets one try through, or refuses it, and counts it at once: checking the limits and taking the place happen
    /// under one lock, so tries sent together can't all slip in under the last free place. The caller then checks the
    /// code and calls <see cref="Confirm"/> when it was wrong, or <see cref="Release"/> when the try turned out to be
    /// anything else (a right code, an expired one), which gives the place back.
    /// </summary>
    /// <param name="trustedDevice">The browser carries the cookie of an earlier sign-in with this email.</param>
    public (Slot? Slot, Refusal Refused) Reserve(HttpContext http, string email, bool trustedDevice, bool hasAccount)
    {
        var ip = ClientAddress.Key(http);
        lock (gate)
        {
            // Read under the lock, so each list stays in time order.
            var now = clock.GetUtcNow();
            // A browser this email signed in on isn't held to the per-address cap, until it has had its wrong codes for the day.
            var trusted = trustedDevice && Count(byEmail, email, now - LoginCodes.Day) < LoginCodes.MaxWrongPerDay;
            if (!trusted && Count(byIp, ip, now - Hour) >= perIp) return (null, Refusal.Network);
            if (!hasAccount)
            {
                Trim(newAddresses, now - Hour);
                if (newAddresses.Count >= forNew) return (null, Refusal.NewPaused);
            }
            var slot = new Slot { Trusted = trusted, At = now, New = !hasAccount };
            if (trusted) slot.Email = Add(byEmail, email, now, now - LoginCodes.Day) ? email : null;
            else slot.Ip = Add(byIp, ip, now, now - Hour) ? ip : null;
            if (slot.New)
            {
                newAddresses.Add(now);
                checkingNew++;
            }
            return (slot, Refusal.None);
        }
    }

    /// <summary>The try was not a wrong code: its place is given back.</summary>
    public void Release(Slot slot)
    {
        lock (gate)
        {
            if (slot.Settled) return;
            slot.Settled = true;
            if (slot.Email is not null && byEmail.TryGetValue(slot.Email, out var forEmail)) forEmail.Remove(slot.At);
            if (slot.Ip is not null && byIp.TryGetValue(slot.Ip, out var forIp)) forIp.Remove(slot.At);
            if (slot.New)
            {
                newAddresses.Remove(slot.At);
                checkingNew = Math.Max(0, checkingNew - 1);
            }
        }
    }

    /// <summary>
    /// The try was a wrong code: its place stays taken. Returns true when the pause on first-time sign-ins is now on
    /// and staff haven't been told about one in the last hour.
    /// </summary>
    public bool Confirm(Slot slot)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (slot.Settled) return false;
            slot.Settled = true;
            if (!slot.New) return false;
            checkingNew = Math.Max(0, checkingNew - 1);
            Trim(newAddresses, now - Hour);
            // Only wrong codes make a pause worth telling staff about: tries still being checked may yet give their place back.
            if (newAddresses.Count - checkingNew < forNew) return false;
            if (lastAlert is DateTimeOffset told && now - told < Hour) return false;
            lastAlert = now;
            return true;
        }
    }

    /// <summary>Drops the times at or before the cutoff.</summary>
    static void Trim(List<DateTimeOffset> times, DateTimeOffset cutoff) => times.RemoveAll(t => t <= cutoff);

    static int Count(Dictionary<string, List<DateTimeOffset>> table, string key, DateTimeOffset cutoff)
    {
        if (!table.TryGetValue(key, out var times)) return 0;
        Trim(times, cutoff);
        if (times.Count == 0) table.Remove(key);
        return times.Count;
    }

    /// <summary>Counts one try for a key. False when the table is full and this key isn't tracked.</summary>
    static bool Add(Dictionary<string, List<DateTimeOffset>> table, string key, DateTimeOffset now, DateTimeOffset cutoff)
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
                if (table.Count >= MaxKeys) return false;
            }
            table[key] = times = [];
        }
        // A key at its limit is refused before it gets here, so each key holds about that many times at most.
        times.Add(now);
        return true;
    }
}
