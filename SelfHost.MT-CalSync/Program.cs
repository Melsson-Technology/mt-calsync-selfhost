using System.Threading.RateLimiting;
using Core.MTCalSync;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Don't advertise the server software.
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

builder.Services.AddControllersWithViews();

// DataProtection keys must survive deploys (the publish dir is swapped atomically)
// or every cookie and pending OAuth state dies on each ship.
builder.Services.AddDataProtection()
	.PersistKeysToFileSystem(new DirectoryInfo(Settings.DataProtectionKeysPath))
	.SetApplicationName("MTCalSyncSelfHost");

// Cookie auth for the single operator (see AuthController + PortalAuth).
builder.Services
	.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
	.AddCookie(options =>
	{
		options.LoginPath = "/Auth/Login";
		options.LogoutPath = "/Auth/Logout";
		options.AccessDeniedPath = "/Auth/Login";
		options.ExpireTimeSpan = TimeSpan.FromHours(12);
		options.SlidingExpiration = true;
		options.Cookie.Name = "MTCalSync.SelfHost";
		options.Cookie.HttpOnly = true;
		// Lax, not Strict: the OAuth consent return chain is cross-site-initiated, and
		// Strict withholds the cookie on the callback AND every redirect hop after it.
		// Lax attaches it on top-level GET navigations (the OAuth return path) while
		// still withholding it on cross-site POSTs; state-changing endpoints validate
		// an antiforgery token too. SameAsRequest keeps it working over an SSH tunnel.
		options.Cookie.SameSite = SameSiteMode.Lax;
		options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
	});

builder.Services.AddAuthorization(options =>
{
	// Require auth on every endpoint by default; the login page opts out with
	// [AllowAnonymous].
	options.FallbackPolicy = options.DefaultPolicy;
});

// Per-IP throttle on the login endpoint (real IP courtesy of UseForwardedHeaders).
builder.Services.AddRateLimiter(options =>
{
	options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
	options.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
		ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
		{
			PermitLimit = 10, Window = TimeSpan.FromMinutes(15), QueueLimit = 0
		}));
});

var app = builder.Build();

// TLS terminates at the reverse proxy; trust its forwarded proto/for headers.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
	ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

if (!app.Environment.IsDevelopment())
	app.UseExceptionHandler("/Home/Error");

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Single-tenant: "/" is the operator dashboard (redirects to /Auth/Login when signed out).
app.MapControllerRoute(name: "default", pattern: "{controller=Dashboard}/{action=Index}/{id?}");

// Anonymous liveness probe for external uptime checks: proves Kestrel is up and the
// database answers. No details leak — just ok/degraded.
app.MapGet("/healthz", () =>
{
	var da = new DataAccess();
	da.execScalar("select 1", new Dictionary<string, object>());
	return string.IsNullOrEmpty(da.errorMessage)
		? Results.Text("ok")
		: Results.Text("degraded", statusCode: 503);
}).AllowAnonymous();

app.Run();
