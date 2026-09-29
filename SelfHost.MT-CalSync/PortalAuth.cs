using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace SelfHost.MTCalSync
{
	// Auth for the portal's single operator, who owns everything, so identity lookups are
	// constants: customer 1, user 1, always admin (the ids the engine CLI stamps on operator
	// rows). The signatures suit a multi-user host, so controllers can be shared unchanged.
	public static class PortalAuth
	{
		// The engine's seeded customer 1 and its operator.
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
