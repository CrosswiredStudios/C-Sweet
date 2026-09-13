using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace CSweet.Api.Agents;

public static class AgentRateLimiting
{
    public const string ImportPolicy = "agent-import";
    public const string BuildPolicy = "agent-build";
    public const string RunPolicy = "agent-run";
    public const string AuthPolicy = "authentication";

    public static IServiceCollection AddAgentRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            AddFixedWindow(options, ImportPolicy, 10);
            AddFixedWindow(options, BuildPolicy, 5);
            AddFixedWindow(options, RunPolicy, 20);
            AddFixedWindow(options, AuthPolicy, 10);
        });
        return services;
    }

    private static void AddFixedWindow(RateLimiterOptions options, string policyName, int permitLimit)
    {
        options.AddPolicy(policyName, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    }
}
