using Microsoft.AspNetCore.Antiforgery;

namespace CSweet.Api.Auth;

public sealed class ApiAntiforgeryMiddleware
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Get, HttpMethods.Head, HttpMethods.Options, HttpMethods.Trace
    };

    private readonly RequestDelegate _next;

    public ApiAntiforgeryMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery, IWebHostEnvironment environment)
    {
        if (!environment.IsEnvironment("Testing") &&
            context.User.Identity?.IsAuthenticated == true &&
            !IsMobileDispatch(context) &&
            context.Request.Path.StartsWithSegments("/api") &&
            !SafeMethods.Contains(context.Request.Method))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await Results.Problem(
                    title: "Invalid antiforgery token.",
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = "invalid_antiforgery_token" })
                    .ExecuteAsync(context);
                return;
            }
        }

        await _next(context);
    }

    /// <summary>
    /// Phone requests carry no cookie and no token: they come from the in-process dispatcher (a request feature that
    /// ordinary HTTP cannot set) with the device scheme's principal. Cookie requests are never exempt.
    /// </summary>
    private static bool IsMobileDispatch(HttpContext context) =>
        context.Features.Get<MobileDispatchFeature>() is not null &&
        context.User.Identity?.AuthenticationType == MobileRequestDispatcher.AuthenticationType;
}
