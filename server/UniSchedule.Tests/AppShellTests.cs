using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace UniSchedule.Tests;

/// <summary>How the web app's files are sent: the page itself must be checked again on every visit, so a deploy shows at once.</summary>
public class AppShellTests
{
    [Fact]
    public async Task Index_html_is_revalidated_but_hashed_scripts_are_not_marked()
    {
        var root = Path.Combine(Path.GetTempPath(), "unischedule-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><title>Classy</title>");
        await File.WriteAllTextAsync(Path.Combine(root, "assets", "index-abc123.js"), "console.log(1)");
        try
        {
            using var factory = new ApiFactory();
            using var app = factory.WithWebHostBuilder(b => b.UseWebRoot(root));
            var c = app.CreateClient();

            // "/" (default file), "/index.html" (static file) and a client route (the fallback) all send the page.
            foreach (var path in new[] { "/", "/index.html", "/setup" })
            {
                var r = await c.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                Assert.True(r.Headers.CacheControl?.NoCache == true, $"{path} should send Cache-Control: no-cache");
            }

            var js = await c.GetAsync("/assets/index-abc123.js");
            Assert.Equal(HttpStatusCode.OK, js.StatusCode);
            Assert.Null(js.Headers.CacheControl);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
