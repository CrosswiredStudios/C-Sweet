using CSweet.Application.GenAi;
using CSweet.Contracts.GenAi;
using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.GenAi;

public sealed class LocalGenAiProviderDiscoveryService(
    IGenAiProviderProfileService providerProfiles) : ILocalGenAiProviderDiscoveryService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private const string ProviderName = "ComfyUI Local";
    private const string DefaultBaseUrl = "http://localhost:8188";
    private const string Loopback = "127.0.0.1";
    private const string Localhost = "localhost";
    private const string DockerHost = "host.docker.internal";

    public async Task<LocalGenAiProviderDiscoveryResponse> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var candidates = CandidateBaseUrls();
        var profiles = (await providerProfiles.ListAsync(cancellationToken)).ToList();
        var existing = FindExisting(profiles, candidates);
        if (existing is not null)
        {
            return Response(profiles, new(
                GenAiProviderType.ComfyUiLocal,
                ProviderName,
                existing.BaseUrl,
                LocalGenAiProviderDiscoveryStatuses.AlreadyConfigured,
                "This local ComfyUI runtime is already configured."));
        }

        var probes = await Task.WhenAll(candidates.Select((baseUrl, priority) =>
            ProbeAsync(baseUrl, priority, cancellationToken)));
        cancellationToken.ThrowIfCancellationRequested();
        var successfulProbe = probes
            .Where(x => x.Result.Succeeded)
            .OrderBy(x => x.Priority)
            .FirstOrDefault();
        if (successfulProbe is null)
        {
            return Response(profiles, new(
                GenAiProviderType.ComfyUiLocal,
                ProviderName,
                null,
                LocalGenAiProviderDiscoveryStatuses.NotFound,
                "No running ComfyUI endpoint was found."));
        }

        profiles = (await providerProfiles.ListAsync(cancellationToken)).ToList();
        existing = FindExisting(profiles, candidates);
        if (existing is not null)
        {
            return Response(profiles, new(
                GenAiProviderType.ComfyUiLocal,
                ProviderName,
                existing.BaseUrl,
                LocalGenAiProviderDiscoveryStatuses.AlreadyConfigured,
                "This local ComfyUI runtime is already configured."));
        }

        var created = await providerProfiles.CreateAsync(new(
            ProviderName,
            GenAiProviderType.ComfyUiLocal,
            successfulProbe.BaseUrl,
            null), cancellationToken);
        profiles = (await providerProfiles.ListAsync(cancellationToken)).ToList();

        return created.Succeeded
            ? Response(profiles, new(
                GenAiProviderType.ComfyUiLocal,
                ProviderName,
                successfulProbe.BaseUrl,
                LocalGenAiProviderDiscoveryStatuses.Added,
                "Connected ComfyUI automatically."))
            : Response(profiles, new(
                GenAiProviderType.ComfyUiLocal,
                ProviderName,
                successfulProbe.BaseUrl,
                LocalGenAiProviderDiscoveryStatuses.NotFound,
                created.Message ?? "The detected ComfyUI provider could not be saved."));
    }

    private async Task<ProbeResult> ProbeAsync(
        string baseUrl,
        int priority,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        var result = await providerProfiles.TestDraftAsync(new(
            null,
            GenAiProviderType.ComfyUiLocal,
            baseUrl,
            null), timeout.Token);
        return new(baseUrl, priority, result);
    }

    private static IReadOnlyList<string> CandidateBaseUrls()
    {
        var hosts = IsRunningInContainer()
            ? new[] { DockerHost, Loopback, Localhost }
            : new[] { Loopback, Localhost, DockerHost };
        return hosts.Select(host => ReplaceHost(DefaultBaseUrl, host)).ToList();
    }

    private static bool IsRunningInContainer() =>
        string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static string ReplaceHost(string baseUrl, string host)
    {
        var builder = new UriBuilder(baseUrl) { Host = host };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static GenAiProviderProfileResponse? FindExisting(
        IEnumerable<GenAiProviderProfileResponse> profiles,
        IReadOnlyList<string> candidates) =>
        profiles.FirstOrDefault(profile =>
            profile.ProviderType == GenAiProviderType.ComfyUiLocal &&
            candidates.Any(candidate => UrlsMatch(profile.BaseUrl, candidate)));

    private static bool UrlsMatch(string left, string right) =>
        string.Equals(
            left.Trim().TrimEnd('/'),
            right.Trim().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    private static LocalGenAiProviderDiscoveryResponse Response(
        IReadOnlyList<GenAiProviderProfileResponse> profiles,
        LocalGenAiProviderDiscoveryResult result) =>
        new(profiles, [result]);

    private sealed record ProbeResult(
        string BaseUrl,
        int Priority,
        GenAiConnectionTestResponse Result);
}
