using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CSweet.TrustedServices;

/// <summary>
/// A separate trust domain for AgentHost-to-Core calls. This key must never be accepted by GitHost
/// or ProvisionerHost, so compromise of the agent-facing broker cannot grant provider authority.
/// </summary>
public sealed class AgentBrokerAuthenticationOptions
{
    public const string SectionName = "AgentBrokerAuthentication";
    public string KeyId { get; set; } = "agenthost";
    public string SharedKeyBase64 { get; set; } = string.Empty;
    public int AllowedClockSkewSeconds { get; set; } = 120;
}

public static class AgentBrokerAuthenticationExtensions
{
    public static IServiceCollection AddAgentBrokerAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AgentBrokerAuthenticationOptions>()
            .Bind(configuration.GetSection(AgentBrokerAuthenticationOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.KeyId), "An AgentHost key ID is required.")
            .Validate(options => TryGetKey(options, out _),
                "The AgentHost shared key must be base64-encoded and at least 32 bytes.")
            .Validate(options => options.AllowedClockSkewSeconds is >= 30 and <= 300,
                "Allowed clock skew must be between 30 and 300 seconds.")
            .ValidateOnStart();
        services.AddSingleton<TrustedRequestReplayCache>();
        services.AddTransient<AgentBrokerAuthenticationHandler>();
        return services;
    }

    public static IApplicationBuilder UseAgentBrokerAuthentication(this IApplicationBuilder app) =>
        app.UseMiddleware<AgentBrokerAuthenticationMiddleware>();

    internal static bool TryGetKey(AgentBrokerAuthenticationOptions options, out byte[] key)
    {
        try
        {
            key = Convert.FromBase64String(options.SharedKeyBase64);
            return key.Length >= 32;
        }
        catch (FormatException)
        {
            key = [];
            return false;
        }
    }
}

public sealed class AgentBrokerAuthenticationMiddleware(
    RequestDelegate next,
    IOptions<AgentBrokerAuthenticationOptions> options,
    TrustedRequestReplayCache replayCache,
    TimeProvider timeProvider)
{
    private readonly AgentBrokerAuthenticationOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/agent-broker"))
        {
            await next(context);
            return;
        }

        var keyId = context.Request.Headers[TrustedServiceHeaders.KeyId].ToString();
        var timestampText = context.Request.Headers[TrustedServiceHeaders.Timestamp].ToString();
        var nonce = context.Request.Headers[TrustedServiceHeaders.Nonce].ToString();
        var signatureText = context.Request.Headers[TrustedServiceHeaders.Signature].ToString();
        if (!AgentBrokerAuthenticationExtensions.TryGetKey(_options, out var key) ||
            !string.Equals(keyId, _options.KeyId, StringComparison.Ordinal) ||
            !long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds) ||
            nonce.Length is < 16 or > 128 || signatureText.Length == 0)
        {
            await RejectAsync(context);
            return;
        }

        var allowedSkew = TimeSpan.FromSeconds(_options.AllowedClockSkewSeconds);
        var sentAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if ((timeProvider.GetUtcNow() - sentAt).Duration() > allowedSkew)
        {
            await RejectAsync(context);
            return;
        }

        context.Request.EnableBuffering();
        var digest = await SHA256.HashDataAsync(context.Request.Body, context.RequestAborted);
        context.Request.Body.Position = 0;
        var canonical = TrustedServiceSignature.CreateCanonical(
            context.Request.Method,
            context.Request.Path + context.Request.QueryString,
            timestampText,
            nonce,
            Convert.ToHexString(digest).ToLowerInvariant());
        byte[] supplied;
        try
        {
            supplied = Convert.FromBase64String(signatureText);
        }
        catch (FormatException)
        {
            await RejectAsync(context);
            return;
        }
        if (!CryptographicOperations.FixedTimeEquals(
                TrustedServiceSignature.Compute(key, canonical), supplied) ||
            !replayCache.TryUse(keyId, nonce, allowedSkew + allowedSkew))
        {
            await RejectAsync(context);
            return;
        }
        await next(context);
    }

    private static Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new { error = "agent_broker_authentication_failed" });
    }
}

public sealed class AgentBrokerAuthenticationHandler(
    IOptions<AgentBrokerAuthenticationOptions> options,
    TimeProvider timeProvider) : DelegatingHandler
{
    private readonly AgentBrokerAuthenticationOptions _options = options.Value;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!AgentBrokerAuthenticationExtensions.TryGetKey(_options, out var key))
            throw new InvalidOperationException("Agent broker authentication is not configured with a valid shared key.");
        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        if (request.Content is not null)
        {
            var replacement = new ByteArrayContent(body);
            foreach (var header in request.Content.Headers)
                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Content = replacement;
        }
        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var canonical = TrustedServiceSignature.CreateCanonical(
            request.Method.Method,
            request.RequestUri?.PathAndQuery
                ?? throw new InvalidOperationException("Agent broker requests require a URI."),
            timestamp,
            nonce,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
        request.Headers.TryAddWithoutValidation(TrustedServiceHeaders.KeyId, _options.KeyId);
        request.Headers.TryAddWithoutValidation(TrustedServiceHeaders.Timestamp, timestamp);
        request.Headers.TryAddWithoutValidation(TrustedServiceHeaders.Nonce, nonce);
        request.Headers.TryAddWithoutValidation(
            TrustedServiceHeaders.Signature,
            Convert.ToBase64String(TrustedServiceSignature.Compute(key, canonical)));
        return await base.SendAsync(request, cancellationToken);
    }
}
