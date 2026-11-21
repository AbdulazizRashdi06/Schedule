namespace UniSchedule.Api.Auth;

/// <summary>
/// Tries counted in memory, keyed by text: <c>limit</c> tries within <c>window</c> lock the key for <c>window</c>.
/// Kept small: an entry goes once its window and lock are over, and the table never holds more than <c>maxEntries</c>
/// (the oldest goes first).
/// </summary>
public sealed class TryTable(int limit, TimeSpan window, int maxEntries = 20_000)
{
    readonly Dictionary<string, (int Tries, DateTimeOffset Since, DateTimeOffset Until)> entries = [];
    DateTimeOffset lastPurge;

    public int Count { get { lock (entries) return entries.Count; } }

    public bool IsLocked(string key, DateTimeOffset now)
    {
        lock (entries) return entries.TryGetValue(key, out var e) && e.Until > now;
    }

    /// <summary>Counts a try, or refuses it (false) while the key is locked. The try that reaches the limit is
    /// still allowed and starts the lock.</summary>
    public bool TryAdd(string key, DateTimeOffset now)
    {
        lock (entries)
        {
            Purge(now);
            if (entries.TryGetValue(key, out var e))
            {
                if (e.Until > now) return false;
                if (e.Since <= now - window) e = (0, now, default);
            }
            else
            {
                if (entries.Count >= maxEntries) entries.Remove(entries.MinBy(x => x.Value.Since).Key);
                e = (0, now, default);
            }
            e.Tries++;
            entries[key] = e.Tries >= limit ? (0, now, now + window) : e;
            return true;
        }
    }

    public void Clear(string key)
    {
        lock (entries) entries.Remove(key);
    }

    void Purge(DateTimeOffset now)
    {
        if (now - lastPurge < TimeSpan.FromMinutes(1) && entries.Count < maxEntries) return;
        lastPurge = now;
        foreach (var (key, e) in entries)
            if (e.Until <= now && e.Since <= now - window) entries.Remove(key);
    }
}
