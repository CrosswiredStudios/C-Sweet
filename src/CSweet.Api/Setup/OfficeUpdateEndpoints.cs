using System.Text.Json;
using CSweet.Contracts.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.Api.Setup;

public static class OfficeUpdateEndpoints
{
    public static async Task<IResult> CheckAsync(CSweetDbContext db, IHttpClientFactory clients,
        IOptions<ExecutionFleetOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        var nodes = await db.ExecutionNodes.AsNoTracking().Where(x => x.RevokedAt == null).ToListAsync(cancellationToken);
        var localVersion = LocalOfficeDevelopmentSource.ReadVersion(options.Value);
        var localPackages = localVersion is null ? Array.Empty<OfficeUpdatePackageResponse>() : nodes
            .Where(x => LocalOfficeDevelopmentSource.MatchesHost(x.MachineName, x.OperatingSystem, x.Architecture))
            .Select(x => new OfficeUpdatePackageResponse(x.Id, null, localVersion, "local-setup")).ToArray();
        IResult Unavailable(string message) => localPackages.Length > 0
            ? Results.Ok(new OfficeUpdateCheckResponse(localVersion!, clock.GetUtcNow(), localPackages, "local-setup",
                "Published Office releases are unavailable. Updates for this computer use the same local source as initial setup."))
            : Results.Problem(message, statusCode: 503);

        if (!Uri.TryCreate(options.Value.ReleaseManifestUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
            return Unavailable("An HTTPS Office release manifest must be configured.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var client = clients.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new() { NoCache = true };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return Unavailable("No published Office release manifest is available. Publish an Office installer with office-release.json before updating remote Offices.");
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return Unavailable("Access to the Office release manifest was denied. Configure a release source that headquarters can access.");
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != "https")
                throw new InvalidDataException("The release manifest redirected to an insecure URL.");
            await response.Content.LoadIntoBufferAsync(1024 * 1024, timeout.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var manifest = document.RootElement;
            var version = ReadReleaseVersion(manifest);
            if (version is null) throw new InvalidDataException("The Office release manifest is invalid or incompatible.");
            var packages = nodes.Select(node => new OfficeUpdatePackageResponse(node.Id,
                ExecutionFleetEndpoints.FindRecoveryPackage(manifest, node.OperatingSystem, node.Architecture) ?? ""))
                .Where(x => !string.IsNullOrEmpty(x.Url) && !localPackages.Any(local => local.OfficeId == x.OfficeId)).Concat(localPackages).ToArray();
            return Results.Ok(new OfficeUpdateCheckResponse(version, clock.GetUtcNow(), packages));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
            JsonException or InvalidOperationException or InvalidDataException)
        {
            return Unavailable("Could not check Office releases. Try again shortly.");
        }
    }

    internal static string? ReadReleaseVersion(JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object ||
            !manifest.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1 ||
            !manifest.TryGetProperty("protocolVersion", out var protocol) || protocol.ValueKind != JsonValueKind.String ||
            protocol.GetString() != "1.0" ||
            !manifest.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array ||
            !manifest.TryGetProperty("officeVersion", out var version) || version.ValueKind != JsonValueKind.String)
            return null;
        var text = version.GetString();
        return text is { Length: <= 64 } && text.Split('.').Length == 3 &&
            Version.TryParse(text, out _) ? text : null;
    }
}
