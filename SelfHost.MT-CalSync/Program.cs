using System.Threading.RateLimiting;
using Core.MTCalSync;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Don't advertise the server software.
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

builder.Services.AddControllersWithViews();

// The antiforgery cookie has no Secure flag by default. SameAsRequest sets it behind TLS
// (including the proxy's forwarded proto) and still works over plain HTTP on an SSH tunnel.
builder.Services.AddAntiforgery(o => o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest);

// DataProtection keys live outside the publish dir, which each deploy replaces; otherwise
// every deploy invalidates cookies and pending OAuth state.
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
		// Lax, not Strict: the OAuth return chain is cross-site, and Strict withholds the
		// cookie on the callback and every redirect after it. Lax still withholds it on
		// cross-site POSTs, and state-changing endpoints also validate an antiforgery token.
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
{
	app.UseExceptionHandler("/Home/Error");
	// HSTS applies only over HTTPS and never to localhost, so tunnels are unaffected. The
	// default 30 days, not a year, so a later hostname or tunnel change doesn't lock the
	// operator's browser out of plain HTTP.
	app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// "/" is the operator dashboard (redirects to /Auth/Login when signed out).
app.MapControllerRoute(name: "default", pattern: "{controller=Dashboard}/{action=Index}/{id?}");

// Anonymous liveness probe: Kestrel is up and the database answers. Returns only
// ok or degraded.
app.MapGet("/healthz", () =>
{
	var da = new DataAccess();
	da.execScalar("select 1", new Dictionary<string, object>());
	return string.IsNullOrEmpty(da.errorMessage)
		? Results.Text("ok")
		: Results.Text("degraded", statusCode: 503);
}).AllowAnonymous();

app.Run();
