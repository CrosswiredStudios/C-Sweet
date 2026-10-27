using System.Net.Http.Json;
using CSweet.Contracts.Plugins;
namespace CSweet.UI.Services;
public sealed class RemoteMcpAccountApiClient(HttpClient http)
{
    private static string Path(Guid business, Guid provider) => $"api/businesses/{business:D}/plugins/{provider:D}/api-key-accounts/";
    public Task<RemoteMcpAccountView> GetAsync(Guid business, Guid provider) => Send<RemoteMcpAccountView>(HttpMethod.Get, Path(business, provider));
    public async Task ConnectAsync(Guid business, Guid provider, RemoteMcpKeyRequest input) => await Send<RemoteMcpConnectionView>(HttpMethod.Post, Path(business, provider), input);
    public async Task RotateAsync(Guid business, Guid provider, Guid connection, RemoteMcpKeyRequest input) => await Send<object>(HttpMethod.Put, Path(business, provider) + $"{connection:D}/key", input);
    public async Task GrantAsync(Guid business, Guid provider, Guid connection, RemoteMcpGrantRequest input) => await Send<object>(HttpMethod.Put, Path(business, provider) + $"{connection:D}/grant", input);
    public async Task RevokeAsync(Guid business, Guid provider, Guid connection, RemoteMcpRevokeRequest input) => await Send<object>(HttpMethod.Post, Path(business, provider) + $"{connection:D}/revoke", input);
    private async Task<T> Send<T>(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("X-CSweet-Native-Account", "1");
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Account access is unavailable or changed. Refresh and review access again.");
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return default!;
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidOperationException("Account settings are unavailable.");
    }
}
