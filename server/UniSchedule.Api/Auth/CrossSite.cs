namespace UniSchedule.Api.Auth;

/// <summary>
/// Spots requests that change things and that a browser sent from another site with our cookies. SameSite=Lax only
/// stops other sites; a page on a sibling subdomain counts as the same site and would get the cookie.
/// </summary>
public static class CrossSite
{
    public static bool IsForeign(HttpRequest r)
    {
        if (!(HttpMethods.IsPost(r.Method) || HttpMethods.IsPut(r.Method) || HttpMethods.IsPatch(r.Method) || HttpMethods.IsDelete(r.Method))) return false;
        if (!r.Path.StartsWithSegments("/api")) return false;
        // Only cookies are sent without asking: the sync token (Authorization: Bearer) can't be added by another site.
        if (r.Headers.Cookie.Count == 0) return false;
        if (r.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;

        var origin = r.Headers.Origin.ToString();
        // The Classy Sync extension's Connect button posts with the owner's cookie. No web page can use these origins.
        if (origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
            || origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase)) return false;

        var fetchSite = r.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0 && fetchSite is not ("same-origin" or "none")) return true;
        // Behind Caddy, UseForwardedHeaders has already made these the public scheme and host.
        if (origin.Length > 0 && !string.Equals(origin, $"{r.Scheme}://{r.Host}", StringComparison.OrdinalIgnoreCase)) return true;
        // Neither header: not a browser page (curl, the tests), so there's no one else's cookie to misuse.
        return false;
    }
}
