using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.SourceControl;

namespace CSweet.UI.Services;

public sealed record SourceControlSettingsState(InternalGitStorageStatus? Storage, PlatformSourceControlSetupResponse? Setup,
    bool AccessDenied, string? StorageError, string? SetupError)
{
    public static async Task<SourceControlSettingsState> LoadAsync(HttpClient http, CancellationToken ct = default)
    {
        var storage = ReadAsync<InternalGitStorageStatus>("api/source-control/storage");
        var setup = ReadAsync<PlatformSourceControlSetupResponse>("api/source-control/platform-setup/");
        await Task.WhenAll(storage, setup);
        if (storage.Result.Denied || setup.Result.Denied) return new(null, null, true, null, null);
        return new(storage.Result.Value, setup.Result.Value, false,
            storage.Result.Value is null ? "GitHost storage status is unavailable. Check the service and retry." : null,
            setup.Result.Value is null ? "Optional GitHub setup is unavailable. Internal Git storage is checked separately." : null);

        async Task<(T? Value, bool Denied)> ReadAsync<T>(string endpoint) where T : class
        {
            try
            {
                using var response = await http.GetAsync(endpoint, ct);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return (null, true);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<T>(ct), false);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            { return (null, false); }
        }
    }
}
