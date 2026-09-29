using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace CSweet.AgentHost.Broker;

/// <summary>Throttle anonymous ingress, not authenticated agent coordination and lease traffic.</summary>
internal sealed class McpIngressPolicy : IRateLimiterPolicy<string>
{
    internal sealed record Authentication(AgentSession? Session);

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => null;

    public RateLimitPartition<string> GetPartition(HttpContext context)
    {
        // A caller-supplied session header is not proof of authentication.
        if (context.Features.Get<Authentication>()?.Session is not null)
            return RateLimitPartition.GetNoLimiter("authenticated");

        return RateLimitPartition.GetSlidingWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 240,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
                AutoReplenishment = true
            });
    }

    internal static async Task AuthenticateAsync(HttpContext context, McpAgentSessionService sessions)
    {
        const string prefix = "Bearer ";
        var authorization = context.Request.Headers.Authorization.ToString();
        var session = authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? await sessions.AuthenticateAsync(authorization[prefix.Length..].Trim(),
                context.Request.Headers["Mcp-Session-Id"].FirstOrDefault(), context.RequestAborted)
            : null;
        context.Features.Set(new Authentication(session));
    }
}
