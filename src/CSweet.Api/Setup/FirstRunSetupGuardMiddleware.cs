using CSweet.Application.Setup;

namespace CSweet.Api.Setup;

public sealed class FirstRunSetupGuardMiddleware
{
    private static readonly string[] AllowedApiPrefixes =
    [
        "/api/setup",
        "/api/execution-nodes",
        "/api/auth",
        "/api/core",
        "/api/llm-provider-profiles",
        "/api/genai-provider-profiles",
        "/api/plugins",
        "/api/model-capability-tests",
        "/api/organizations",
        "/api/tasks",
        "/api/artifacts",
        "/api/planning-runs",
        "/api/documents",
        "/api/planning-workflows"
    ];

    private readonly RequestDelegate _next;

    public FirstRunSetupGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ISetupService setupService)
    {
        if (!ShouldGuard(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var status = await setupService.GetStatusAsync(context.RequestAborted);

        if (status.IsFirstRunComplete)
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await Results.Problem(
            title: "System setup is incomplete.",
            detail: "Complete first-run setup before using this endpoint.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>
            {
                ["errorCode"] = "configuration_incomplete"
            }).ExecuteAsync(context);
    }

    private static bool ShouldGuard(PathString path)
    {
        if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.Equals("/api/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !AllowedApiPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
