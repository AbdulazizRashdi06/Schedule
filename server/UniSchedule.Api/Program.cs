using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api;
using UniSchedule.Api.Admin;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Students;

var builder = WebApplication.CreateBuilder(args.FirstOrDefault() == Cli.CreateAdmin ? [] : args);
var config = builder.Configuration;

// ---- Storage: one SQLite file, data protection keys next to it ----
var dbPath = Path.GetFullPath(config["Data:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "app-data", "schedule.db"));
var dataDir = Path.GetDirectoryName(dbPath)!;
Directory.CreateDirectory(dataDir);
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(config["Data:KeysPath"] ?? Path.Combine(dataDir, "keys")))
    .SetApplicationName("UniSchedule");

builder.Services.AddSingleton(TimeProvider.System);
// Outside APIs (timetable, student courses) that owners connect in the dashboard.
builder.Services.AddHttpClient<IConnectionFetcher, HttpConnectionFetcher>(c => c.Timeout = TimeSpan.FromSeconds(30));
if (!builder.Environment.IsEnvironment("Testing")) builder.Services.AddHostedService<ConnectionScheduler>();
builder.Services.Configure<EmailOptions>(config.GetSection("Email"));
if (string.Equals(config["Email:Provider"], "resend", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHttpClient<IEmailSender, ResendEmailSender>(c => c.Timeout = TimeSpan.FromSeconds(15));
else
    builder.Services.AddSingleton<IEmailSender, LogEmailSender>();

// ---- Sign-in: one cookie, roles checked against the database on each request ----
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "unischedule";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromDays(60);
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = Session.ValidateAsync;
        // An API: answer with status codes instead of redirecting to a login page.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Session.StudentPolicy, p => p.RequireClaim(Session.KindClaim, "student"))
    // Staff with a temporary password may only change it.
    .AddPolicy(Session.StaffPolicy, p => p.RequireClaim(Session.RoleClaim, "owner", "rsa").RequireAssertion(c => !c.User.HasClaim(x => x.Type == Session.MustChangeClaim)))
    .AddPolicy(Session.OwnerPolicy, p => p.RequireClaim(Session.RoleClaim, "owner").RequireAssertion(c => !c.User.HasClaim(x => x.Type == Session.MustChangeClaim)));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
        await ctx.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many tries. Wait a minute and try again." }, ct);
    // Per client IP, per 5 minutes. RateLimits:RequestCode / RateLimits:Verify override the defaults.
    static RateLimitPartition<string> PerIp(HttpContext http, string key, int fallback) =>
        RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = http.RequestServices.GetRequiredService<IConfiguration>().GetValue($"RateLimits:{key}", fallback),
            Window = TimeSpan.FromMinutes(5),
        });
    o.AddPolicy(AuthEndpoints.RequestCodeLimit, http => PerIp(http, "RequestCode", 10));
    o.AddPolicy(AuthEndpoints.VerifyLimit, http => PerIp(http, "Verify", 20));
});

// Behind Caddy on the Docker network: trust its X-Forwarded-* headers.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = AdminEndpoints.MaxUploadBytes + 64 * 1024);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.MigrateAsync();
    if (args.FirstOrDefault() == Cli.CreateAdmin) return await Cli.RunCreateAdmin(db, args.Skip(1).ToArray());
    if (app.Environment.IsDevelopment()) await DevSeed.RunAsync(db, app.Environment, app.Logger);
}

app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

var api = app.MapGroup("/api");
api.MapGet("/health", () => Results.Ok(new { ok = true }));
api.MapAuth();
api.MapStudent();
api.MapPersonal();
api.MapAdmin();
api.MapOwner();
api.MapFeedback();
api.MapSync();
api.MapConnections();
api.Map("/{**rest}", () => Errors.NotFound());

// Client-side routes (/, /setup, /admin/...) all load the app.
app.MapFallbackToFile("index.html");

app.Run();
return 0;

public partial class Program;
