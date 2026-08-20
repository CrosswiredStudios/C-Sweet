using System.Text.Json;
using CSweet.AI.Providers;
using CSweet.Application.Llm;
using CSweet.Contracts.Llm;
using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.Llm;

public sealed class LocalLlmProviderDiscoveryService : ILocalLlmProviderDiscoveryService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private const string Localhost = "localhost";
    private const string LoopbackAddress = "127.0.0.1";
    private const string DockerHost = "host.docker.internal";

    private readonly OpenAiCompatibleProviderClient _providerClient;
    private readonly ILlmProviderProfileService _providerProfileService;

    public LocalLlmProviderDiscoveryService(
        OpenAiCompatibleProviderClient providerClient,
        ILlmProviderProfileService providerProfileService)
    {
        _providerClient = providerClient;
        _providerProfileService = providerProfileService;
    }

    public async Task<LocalLlmProviderDiscoveryResponse> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var presets = LlmProviderPresets.AllLocalhost();
        var presetOrder = presets
            .Select((preset, index) => new { preset.ProviderType, Index = index })
            .ToDictionary(item => item.ProviderType, item => item.Index);
        var existingProfiles = (await _providerProfileService.ListAsync(cancellationToken)).ToList();
        var runtimeCandidates = presets.ToDictionary(
            preset => preset.ProviderType,
            CandidateBaseUrls);
        var results = new List<LocalLlmProviderDiscoveryResult>(presets.Count);
        var pending = new List<(LlmProviderPreset Preset, IReadOnlyList<string> Candidates)>();

        foreach (var preset in presets)
        {
            var candidates = runtimeCandidates[preset.ProviderType];
            var existing = existingProfiles.FirstOrDefault(profile =>
                profile.ProviderType == preset.ProviderType &&
                candidates.Any(candidate => UrlsMatch(profile.BaseUrl, candidate)));

            if (existing is not null)
            {
                results.Add(new LocalLlmProviderDiscoveryResult(
                    preset.ProviderType,
                    preset.Name,
                    existing.BaseUrl,
                    LocalLlmProviderDiscoveryStatuses.AlreadyConfigured,
                    0,
                    "This local runtime is already configured."));
                continue;
            }

            pending.Add((preset, candidates));
        }

        var probeTasks = pending
            .SelectMany(runtime => runtime.Candidates.Select((baseUrl, priority) =>
                ProbeAsync(runtime.Preset, baseUrl, priority, cancellationToken)))
            .ToList();
        var probes = await Task.WhenAll(probeTasks);

        foreach (var runtime in pending)
        {
            var successfulProbe = probes
                .Where(probe => probe.ProviderType == runtime.Preset.ProviderType && probe.Models.Count > 0)
                .OrderBy(probe => probe.Priority)
                .FirstOrDefault();

            if (successfulProbe is null)
            {
                results.Add(new LocalLlmProviderDiscoveryResult(
                    runtime.Preset.ProviderType,
                    runtime.Preset.Name,
                    null,
                    LocalLlmProviderDiscoveryStatuses.NotFound,
                    0,
                    "No running model endpoint was found."));
                continue;
            }

            // Refresh before each write so repeated scans and multiple successful aliases
            // cannot create a second copy during this request.
            existingProfiles = (await _providerProfileService.ListAsync(cancellationToken)).ToList();
            var existing = existingProfiles.FirstOrDefault(profile =>
                profile.ProviderType == runtime.Preset.ProviderType &&
                runtime.Candidates.Any(candidate => UrlsMatch(profile.BaseUrl, candidate)));

            if (existing is not null)
            {
                results.Add(new LocalLlmProviderDiscoveryResult(
                    runtime.Preset.ProviderType,
                    runtime.Preset.Name,
                    existing.BaseUrl,
                    LocalLlmProviderDiscoveryStatuses.AlreadyConfigured,
                    successfulProbe.Models.Count,
                    "This local runtime is already configured."));
                continue;
            }

            var createResult = await _providerProfileService.CreateAsync(
                new CreateLlmProviderProfileRequest(
                    runtime.Preset.Name,
                    runtime.Preset.ProviderType,
                    successfulProbe.BaseUrl,
                    runtime.Preset.ApiKeyPlaceholder,
                    string.Empty,
                    null,
                    null,
                    null,
                    runtime.Preset.SupportsStreaming,
                    runtime.Preset.SupportsToolCalling,
                    runtime.Preset.SupportsStructuredOutput,
                    runtime.Preset.SupportsVision),
                cancellationToken);

            results.Add(createResult.Succeeded
                ? new LocalLlmProviderDiscoveryResult(
                    runtime.Preset.ProviderType,
                    runtime.Preset.Name,
                    successfulProbe.BaseUrl,
                    LocalLlmProviderDiscoveryStatuses.Added,
                    successfulProbe.Models.Count,
                    $"Found {successfulProbe.Models.Count} model(s).")
                : new LocalLlmProviderDiscoveryResult(
                    runtime.Preset.ProviderType,
                    runtime.Preset.Name,
                    successfulProbe.BaseUrl,
                    LocalLlmProviderDiscoveryStatuses.NotFound,
                    successfulProbe.Models.Count,
                    createResult.Message ?? "The detected provider could not be saved."));
        }

        return new LocalLlmProviderDiscoveryResponse(
            await _providerProfileService.ListAsync(cancellationToken),
            results
                .OrderBy(result => presetOrder[result.ProviderType])
                .ToList());
    }

    private async Task<ProbeResult> ProbeAsync(
        LlmProviderPreset preset,
        string baseUrl,
        int priority,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        var profile = new LlmProviderProfile
        {
            Id = Guid.NewGuid(),
            Name = preset.Name,
            ProviderType = preset.ProviderType,
            BaseUrl = baseUrl,
            DefaultChatModel = string.Empty,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            var models = await _providerClient.ListModelsAsync(
                profile,
                preset.ApiKeyPlaceholder ?? string.Empty,
                timeout.Token);
            return new ProbeResult(preset.ProviderType, baseUrl, priority, models);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(preset.ProviderType, baseUrl, priority, []);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or LlmProviderHttpException
            or UriFormatException
            or JsonException)
        {
            return new ProbeResult(preset.ProviderType, baseUrl, priority, []);
        }
    }

    private static IReadOnlyList<string> CandidateBaseUrls(LlmProviderPreset preset)
    {
        var hosts = IsRunningInContainer()
            ? new[] { DockerHost, Localhost, LoopbackAddress }
            : new[] { Localhost, LoopbackAddress, DockerHost };

        return hosts
            .Select(host => ReplaceHost(preset.BaseUrl, host))
            .ToList();
    }

    private static bool IsRunningInContainer()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ReplaceHost(string baseUrl, string host)
    {
        var builder = new UriBuilder(baseUrl)
        {
            Host = host
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static bool UrlsMatch(string left, string right)
    {
        return string.Equals(
            left.Trim().TrimEnd('/'),
            right.Trim().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProbeResult(
        LlmProviderType ProviderType,
        string BaseUrl,
        int Priority,
        IReadOnlyList<ModelDescriptor> Models);
}
