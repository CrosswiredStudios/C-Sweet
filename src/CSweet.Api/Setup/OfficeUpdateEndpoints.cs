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
            {
                // Hosted development bundles have a different manifest and remain opt-in.
                // The installed update helper verifies the exact tagged package and certifies
                // the image on its destination host before asking to stop any jobs.
                if (options.Value.AllowUnpinnedDevelopmentImages && uri.AbsoluteUri == OfficialProductionManifest)
                {
                    using var hostedRequest = new HttpRequestMessage(HttpMethod.Get, OfficialHostedManifest);
                    hostedRequest.Headers.CacheControl = new() { NoCache = true };
                    using var hosted = await client.SendAsync(hostedRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if (hosted.IsSuccessStatusCode && TrustedHostedRedirect(hosted.RequestMessage?.RequestUri))
                    {
                        await hosted.Content.LoadIntoBufferAsync(1024 * 1024, timeout.Token);
                        using var hostedDocument = JsonDocument.Parse(await hosted.Content.ReadAsStringAsync(timeout.Token));
                        var hostedVersion = ReadHostedReleaseVersion(hostedDocument.RootElement);
                        if (hostedVersion is not null)
                        {
                            var url = $"https://github.com/CrosswiredStudios/CSweet.Office/releases/download/v{hostedVersion}/office-bootstrap.json";
                            var hostedPackages = nodes.Where(x => x.OperatingSystem == "windows" && x.Architecture == "x64")
                                .Select(x => new OfficeUpdatePackageResponse(x.Id, url, hostedVersion)).ToArray();
                            return Results.Ok(new OfficeUpdateCheckResponse(hostedVersion, clock.GetUtcNow(), hostedPackages,
                                Warning: "Published development bundle. The destination Office must certify its image before installation."));
                        }
                    }
                }
                return Unavailable("No published Office release manifest is available. Publish an Office installer with office-release.json before updating remote Offices.");
            }
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
            // A configured sibling checkout is a fallback, not an override. When a verified
            // published package exists, every compatible Office should use it.
            var publishedPackages = nodes.Select(node => new OfficeUpdatePackageResponse(node.Id,
                ExecutionFleetEndpoints.FindRecoveryPackage(manifest, node.OperatingSystem, node.Architecture) ?? ""))
                .Where(x => !string.IsNullOrEmpty(x.Url)).ToArray();
            var publishedOfficeIds = publishedPackages.Select(x => x.OfficeId).ToHashSet();
            var packages = publishedPackages.Concat(localPackages
                .Where(x => !publishedOfficeIds.Contains(x.OfficeId))).ToArray();
            return Results.Ok(new OfficeUpdateCheckResponse(version, clock.GetUtcNow(), packages));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
            JsonException or InvalidOperationException or InvalidDataException)
        {
            return Unavailable("Could not check Office releases. Try again shortly.");
        }
    }

    private const string OfficialProductionManifest = "https://github.com/CrosswiredStudios/CSweet.Office/releases/latest/download/office-release.json";
    private const string OfficialHostedManifest = "https://github.com/CrosswiredStudios/CSweet.Office/releases/latest/download/office-bootstrap.json";
    private static bool TrustedHostedRedirect(Uri? uri) => uri is { Scheme: "https", Port: 443, UserInfo: "" } &&
        (uri.Host == "github.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal));

    internal static string? ReadHostedReleaseVersion(JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object) return null;
        string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
        if (!manifest.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion) || schemaVersion is not (1 or 2) ||
            Text(manifest, "protocolVersion") != "1.0" || Text(manifest, "signing") != "development" ||
            Text(manifest, "guestBrokerProtocolVersion") != "1.1" ||
            !manifest.TryGetProperty("requiresHostCertification", out var certification) || certification.ValueKind != JsonValueKind.True ||
            !manifest.TryGetProperty("environmentProfiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array ||
            !profiles.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "software-development-polyglot-v1") ||
            !manifest.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() is < 2 or > 3)
            return null;
        var version = Text(manifest, "officeVersion");
        if (version is null || version.Length > 64 || version.Split('.').Length != 3 || version.Any(c => c != '.' && !char.IsAsciiDigit(c)) || !Version.TryParse(version, out _)) return null;
        var root = $"https://github.com/CrosswiredStudios/CSweet.Office/releases/download/v{version}/";
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) return null;
            var name = Text(asset, "name");
            if (name is null || !names.Add(name) || name != $"csweet-office-{version}-windows-support.zip" &&
                name != $"csweet-office-{version}-windows-x64.zip" && name != $"csweet-office-{version}-linux-x64.tar.gz") return null;
            if (Text(asset, "sha256") is not { Length: 64 } hash || !hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                !asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes) || bytes <= 0 || bytes > 16L * 1024 * 1024 * 1024) return null;
            var support = name.EndsWith("-support.zip", StringComparison.Ordinal);
            if (asset.TryGetProperty("parts", out var parts))
            {
                if (schemaVersion != 2 || support || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() is < 2 or > 16) return null;
                long total = 0; var index = 0;
                foreach (var part in parts.EnumerateArray())
                {
                    var partName = $"{name}.part{++index:D3}";
                    if (part.ValueKind != JsonValueKind.Object || Text(part, "name") != partName || Text(part, "url") != root + partName ||
                        Text(part, "sha256") is not { Length: 64 } partHash || !partHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                        !part.TryGetProperty("size", out var partSize) || partSize.ValueKind != JsonValueKind.Number || !partSize.TryGetInt64(out var partBytes) || partBytes <= 0 || partBytes > 1024L * 1024 * 1024) return null;
                    total += partBytes;
                }
                if (total != bytes) return null;
            }
            else if (Text(asset, "url") != root + name || bytes >= 2L * 1024 * 1024 * 1024 || support && bytes > 10 * 1024 * 1024) return null;
        }
        return names.Contains($"csweet-office-{version}-windows-support.zip") && names.Contains($"csweet-office-{version}-windows-x64.zip") ? version : null;
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
