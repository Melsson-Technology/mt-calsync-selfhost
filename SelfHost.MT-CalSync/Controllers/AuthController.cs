using Core.MTCalSync;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace SelfHost.MTCalSync.Controllers
{
	// Single-operator login for the self-host portal. One password, stored hashed in
	// the settings table (set with the `set-admin-password` worker command). No user
	// table, no signup, no reset flow — a self-host operator manages their own box.
	[AllowAnonymous]
	public class AuthController : Controller
	{
		[HttpGet]
		public IActionResult Login(string? returnUrl = null)
		{
			if (User.Identity?.IsAuthenticated == true)
				return RedirectToAction("Index", "Dashboard");
			ViewBag.ReturnUrl = returnUrl;
			return View();
		}

		[HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("auth")]
		public async Task<IActionResult> Login(string password, string? returnUrl = null)
		{
			ViewBag.ReturnUrl = returnUrl;
			string hash = Settings.SelfHostAdminPasswordHash;
			if (string.IsNullOrWhiteSpace(hash))
			{
				ViewBag.Error = "No operator password is set yet. On the server run:  mtcs set-admin-password --password <value>";
				return View();
			}
			if (string.IsNullOrEmpty(password) || !PasswordHasher.Verify(password, hash))
			{
				await Task.Delay(Random.Shared.Next(120, 400));   // blunt timing / brute-force
				ViewBag.Error = "Incorrect password.";
				return View();
			}
			await PortalAuth.SignInAsync(HttpContext);
			return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : RedirectToAction("Index", "Dashboard");
		}

		[HttpPost, ValidateAntiForgeryToken]
		public async Task<IActionResult> Logout()
		{
			await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
			return RedirectToAction("Login");
		}
	}
}
