using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace SelfHost.MTCalSync
{
	// Single-tenant auth for the self-host portal. There is exactly one operator, who
	// owns everything, so tenant lookups collapse to constants — customer 1 / user 1,
	// always admin (the same identifiers the engine CLI stamps on operator rows). The
	// hosted (SaaS) portal has its own multi-tenant PortalAuth; this is the self-host
	// counterpart, kept name- and signature-compatible so the shared controllers copied
	// from it need no change at their call sites.
	public static class PortalAuth
	{
		// The built-in self-host tenant/operator (see the engine's seeded customer 1).
		public const long SelfHostCustomerId = 1;
		public const long SelfHostUserId = 1;

		public static async Task SignInAsync(HttpContext http)
		{
			var claims = new List<Claim>
			{
				new Claim(ClaimTypes.NameIdentifier, SelfHostUserId.ToString()),
				new Claim(ClaimTypes.Name, "Operator"),
				new Claim(ClaimTypes.Role, "Admin")
			};
			var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
			await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
		}

		public static long UserId(ClaimsPrincipal principal) => SelfHostUserId;
		public static long CustomerId(ClaimsPrincipal principal) => SelfHostCustomerId;
		public static bool IsAdmin(ClaimsPrincipal principal) => true;
	}
}
