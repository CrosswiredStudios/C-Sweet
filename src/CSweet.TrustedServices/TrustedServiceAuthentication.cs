using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CSweet.TrustedServices;

public sealed class TrustedServiceAuthenticationOptions
{
    public const string SectionName = "TrustedServiceAuthentication";
    public string KeyId { get; set; } = "core";
    public string SharedKeyBase64 { get; set; } = string.Empty;
    public int AllowedClockSkewSeconds { get; set; } = 120;
}

public static class TrustedServiceHeaders
{
    public const string KeyId = "X-CSweet-Key-Id";
    public const string Timestamp = "X-CSweet-Timestamp";
    public const string Nonce = "X-CSweet-Nonce";
    public const string Signature = "X-CSweet-Signature";
}

public static class TrustedServiceAuthenticationExtensions
{
    public static IServiceCollection AddTrustedServiceAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<TrustedServiceAuthenticationOptions>()
            .Bind(configuration.GetSection(TrustedServiceAuthenticationOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.KeyId), "A key ID is required.")
            .Validate(options => TryGetKey(options, out _), "The shared key must be base64-encoded and at least 32 bytes.")
            .Validate(options => options.AllowedClockSkewSeconds is >= 30 and <= 300,
                "Allowed clock skew must be between 30 and 300 seconds.")
            .ValidateOnStart();
        services.AddSingleton<TrustedRequestReplayCache>();
        services.AddTransient<TrustedServiceAuthenticationHandler>();
        return services;
    }

    public static IApplicationBuilder UseTrustedServiceAuthentication(this IApplicationBuilder app) =>
        app.UseMiddleware<TrustedServiceAuthenticationMiddleware>();

    internal static bool TryGetKey(TrustedServiceAuthenticationOptions options, out byte[] key)
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

public sealed class TrustedRequestReplayCache(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nonces = new(StringComparer.Ordinal);

    public bool TryUse(string keyId, string nonce, TimeSpan lifetime)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var stale in _nonces.Where(entry => entry.Value <= now).Select(entry => entry.Key))
            _nonces.TryRemove(stale, out _);
        return _nonces.TryAdd($"{keyId}:{nonce}", now.Add(lifetime));
    }
}

public sealed class TrustedServiceAuthenticationMiddleware(
    RequestDelegate next,
    IOptions<TrustedServiceAuthenticationOptions> options,
    TrustedRequestReplayCache replayCache,
    TimeProvider timeProvider)
{
    private readonly TrustedServiceAuthenticationOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/internal"))
        {
            await next(context);
            return;
        }

        var keyId = context.Request.Headers[TrustedServiceHeaders.KeyId].ToString();
        var timestampText = context.Request.Headers[TrustedServiceHeaders.Timestamp].ToString();
        var nonce = context.Request.Headers[TrustedServiceHeaders.Nonce].ToString();
        var suppliedSignature = context.Request.Headers[TrustedServiceHeaders.Signature].ToString();
        if (!TrustedServiceAuthenticationExtensions.TryGetKey(_options, out var key) ||
            !string.Equals(keyId, _options.KeyId, StringComparison.Ordinal) ||
            !long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds) ||
            nonce.Length is < 16 or > 128 || suppliedSignature.Length == 0)
        {
            await RejectAsync(context);
            return;
        }

        var now = timeProvider.GetUtcNow();
        var sentAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var allowedSkew = TimeSpan.FromSeconds(_options.AllowedClockSkewSeconds);
        if ((now - sentAt).Duration() > allowedSkew)
        {
            await RejectAsync(context);
            return;
        }

        context.Request.EnableBuffering();
        using var bodyHash = SHA256.Create();
        var digest = await bodyHash.ComputeHashAsync(context.Request.Body, context.RequestAborted);
        context.Request.Body.Position = 0;
        var canonical = TrustedServiceSignature.CreateCanonical(
            context.Request.Method,
            context.Request.Path + context.Request.QueryString,
            timestampText,
            nonce,
            Convert.ToHexString(digest).ToLowerInvariant());
        var expected = TrustedServiceSignature.Compute(key, canonical);
        byte[] supplied;
        try
        {
            supplied = Convert.FromBase64String(suppliedSignature);
        }
        catch (FormatException)
        {
            await RejectAsync(context);
            return;
        }

        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
        {
            await RejectAsync(context);
            return;
        }

        if (!replayCache.TryUse(keyId, nonce, allowedSkew + allowedSkew))
        {
            await RejectAsync(context);
            return;
        }

        await next(context);
    }

    private static Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new { error = "trusted_service_authentication_failed" });
    }
}

public sealed class TrustedServiceAuthenticationHandler(
    IOptions<TrustedServiceAuthenticationOptions> options,
    TimeProvider timeProvider) : DelegatingHandler
{
    private readonly TrustedServiceAuthenticationOptions _options = options.Value;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
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

        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var pathAndQuery = request.RequestUri?.PathAndQuery
            ?? throw new InvalidOperationException("Trusted service requests require an absolute or relative URI.");
        var canonical = TrustedServiceSignature.CreateCanonical(
            request.Method.Method,
            pathAndQuery,
            timestamp,
            nonce,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
        if (!TrustedServiceAuthenticationExtensions.TryGetKey(_options, out var key))
            throw new InvalidOperationException("Trusted service authentication is not configured with a valid shared key.");
        request.Headers.TryAddWithoutValidation(TrustedServiceHeaders.KeyId, _options.KeyId);
        request.Headers.TryAddWithoutValidation(TrustedServiceHeaders.Timestamp, timestamp);
        request.Headers.TryAddWithoutValidation(TrustedServiceHeaders.Nonce, nonce);
        request.Headers.TryAddWithoutValidation(
            TrustedServiceHeaders.Signature,
            Convert.ToBase64String(TrustedServiceSignature.Compute(key, canonical)));
        return await base.SendAsync(request, cancellationToken);
    }
}

internal static class TrustedServiceSignature
{
    public static string CreateCanonical(
        string method,
        string pathAndQuery,
        string timestamp,
        string nonce,
        string bodySha256) =>
        string.Join('\n', method.ToUpperInvariant(), pathAndQuery, timestamp, nonce, bodySha256);

    public static byte[] Compute(byte[] key, string canonical) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical));
}
