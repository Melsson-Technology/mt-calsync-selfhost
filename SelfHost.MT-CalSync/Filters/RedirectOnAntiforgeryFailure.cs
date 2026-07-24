using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;

namespace SelfHost.MTCalSync.Filters
{
	// Antiforgery failures on the auth pages are always benign staleness — a form
	// rendered before the auth state changed (a login tab left open across a
	// sign-in, a cookie cleared mid-session, or a pre-Lax Strict cookie hiding the
	// session on the OAuth return chain). The framework's answer is a bare 400,
	// which is a dead end for the user. Instead: if they're already signed in,
	// send them where they were headed; otherwise re-GET the same page so a fresh
	// token is minted and the retry just works. IAlwaysRunResultFilter because the
	// antiforgery authorization filter short-circuits ordinary result filters.
	public class RedirectOnAntiforgeryFailureAttribute : Attribute, IAlwaysRunResultFilter
	{
		public void OnResultExecuting(ResultExecutingContext context)
		{
			if (context.Result is not IAntiforgeryValidationFailedResult) return;

			if (context.HttpContext.User.Identity?.IsAuthenticated == true)
			{
				// The antiforgery filter already parsed the form (that's where it
				// read the mismatched token), so Request.Form is buffered — no IO.
				string returnUrl = context.HttpContext.Request.HasFormContentType
					? context.HttpContext.Request.Form["returnUrl"].ToString()
					: string.Empty;
				bool safeLocal = returnUrl.StartsWith('/')
					&& !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\");
				context.Result = safeLocal
					? new LocalRedirectResult(returnUrl)
					: new RedirectToActionResult("Index", "Dashboard", null);
			}
			else
			{
				context.Result = new LocalRedirectResult(
					context.HttpContext.Request.Path + context.HttpContext.Request.QueryString);
			}
		}

		public void OnResultExecuted(ResultExecutedContext context) { }
	}
}
