using System.Net.Http.Json;
using CSweet.Contracts.Plugins;

namespace CSweet.UI.Services;

public interface IPluginAccountApiClient
{
    Task<PluginAccountManagementView> GetAsync(Guid installationId, CancellationToken ct = default);
    Task<BeginPluginAccountResponse> ConnectAsync(Guid installationId, ConnectPluginAccountRequest request, CancellationToken ct = default);
    Task RetryAsync(Guid installationId, Guid setupId, CancellationToken ct = default);
    // Compatibility for existing in-process/custom clients. Null means no durable acknowledgment was available.
    async Task<PluginImportResumeReceipt?> ResumeImportAsync(Guid installationId, Guid setupId, long expectedRevision, CancellationToken ct = default)
    { await RetryAsync(installationId, setupId, ct); return null; }
    Task SaveGrantAsync(Guid installationId, Guid accountId, SavePluginAccountGrantRequest request, CancellationToken ct = default);
    Task RevokeGrantAsync(Guid installationId, Guid grantId, long revision, CancellationToken ct = default);
    Task<PluginAccountImpactView> GetDisconnectImpactAsync(Guid installationId, Guid accountId, CancellationToken ct = default);
    Task DisconnectAsync(Guid installationId, Guid accountId, DisconnectPluginAccountRequest request, CancellationToken ct = default);
    Task SaveOperatorAsync(Guid installationId,SavePluginOperatorRequest request,CancellationToken ct=default);
    Task RevokeOperatorAsync(Guid installationId,Guid operatorId,long revision,CancellationToken ct=default);
    Task<PluginInstallationImpact> GetImpactAsync(Guid installationId,CancellationToken ct=default);
    Task ControlAsync(Guid installationId,PluginInstallationControlRequest request,CancellationToken ct=default);
    Task RetryBuildAsync(Guid installationId,CancellationToken ct=default);
    Task<PluginPackageReview> GetReviewAsync(Guid installationId,CancellationToken ct=default);
    Task<PluginAccountUpgradeReview> ReviewUpgradeAsync(Guid installationId,CancellationToken ct=default)=>throw new NotSupportedException();
    Task<PluginAccountUpgradeStatus> UpgradeStatusAsync(Guid installationId,CancellationToken ct=default)=>throw new NotSupportedException();
    Task<PluginAccountUpgradeProgress> ApproveUpgradeAsync(Guid installationId,ApprovePluginAccountUpgrade request,CancellationToken ct=default)=>throw new NotSupportedException();
    Task<PluginAccountUpgradeProgress> ResumeUpgradeAsync(Guid installationId,Guid upgradeId,CancellationToken ct=default)=>throw new NotSupportedException();
    Task<PluginAccountUpgradeProgress> CancelUpgradeAsync(Guid installationId,Guid upgradeId,CancellationToken ct=default)=>throw new NotSupportedException();
}
public sealed class PluginAccountApiClient(HttpClient http) : IPluginAccountApiClient
{
    private static string Path(Guid id) => $"api/plugins/installations/{id:D}/account-management";
    public Task<PluginAccountManagementView> GetAsync(Guid id, CancellationToken ct = default) => Send<PluginAccountManagementView>(HttpMethod.Get, Path(id) + "/", null, ct);
    public Task<PluginAccountUpgradeReview> ReviewUpgradeAsync(Guid id,CancellationToken ct=default)=>SendUpgrade<PluginAccountUpgradeReview>(HttpMethod.Get,Path(id)+"/upgrade/review",null,ct);
    public Task<PluginAccountUpgradeStatus> UpgradeStatusAsync(Guid id,CancellationToken ct=default)=>SendUpgrade<PluginAccountUpgradeStatus>(HttpMethod.Get,Path(id)+"/upgrade",null,ct);
    public Task<PluginAccountUpgradeProgress> ApproveUpgradeAsync(Guid id,ApprovePluginAccountUpgrade request,CancellationToken ct=default)=>SendUpgrade<PluginAccountUpgradeProgress>(HttpMethod.Post,Path(id)+"/upgrade",request,ct);
    public Task<PluginAccountUpgradeProgress> ResumeUpgradeAsync(Guid id,Guid upgradeId,CancellationToken ct=default)=>SendUpgrade<PluginAccountUpgradeProgress>(HttpMethod.Post,Path(id)+$"/upgrade/{upgradeId:D}/resume",null,ct);
    public Task<PluginAccountUpgradeProgress> CancelUpgradeAsync(Guid id,Guid upgradeId,CancellationToken ct=default)=>SendUpgrade<PluginAccountUpgradeProgress>(HttpMethod.Post,Path(id)+$"/upgrade/{upgradeId:D}/cancel",null,ct);
    public Task<BeginPluginAccountResponse> ConnectAsync(Guid id, ConnectPluginAccountRequest request, CancellationToken ct = default) => Send<BeginPluginAccountResponse>(HttpMethod.Post, Path(id) + "/connect", request, ct);
    public async Task RetryAsync(Guid id, Guid setupId, CancellationToken ct = default) => await Send<object>(HttpMethod.Post, Path(id) + $"/setup/{setupId:D}/retry", null, ct);
    public async Task<PluginImportResumeReceipt?> ResumeImportAsync(Guid id, Guid setupId, long expectedRevision, CancellationToken ct = default)
    {
        // ResponseHeadersRead stops HttpClient.Timeout at the headers. Keep a separate
        // end-to-end budget alive through stream acquisition and every receipt-body read.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = http.Timeout;
        if (timeout == Timeout.InfiniteTimeSpan || timeout > TimeSpan.FromSeconds(30)) timeout = TimeSpan.FromSeconds(30);
        deadline.CancelAfter(timeout);
        var operationToken = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(id) + $"/setup/{setupId:D}/retry")
        { Content = JsonContent.Create(new ResumePluginImportRequest(expectedRevision)) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operationToken);
        if (!response.IsSuccessStatusCode)
            throw new ApiClientException(response.StatusCode, "The import resume was not confirmed. Refresh its status before reviewing another attempt.");
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null; // Legacy server: never invent an acknowledgment.
        using var bytes = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(operationToken).WaitAsync(operationToken);
        var buffer = new byte[512];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, operationToken).AsTask().WaitAsync(operationToken);
            if (count == 0) break;
            if (bytes.Length + count > 2048) throw new InvalidOperationException("The import acknowledgment is unavailable.");
            bytes.Write(buffer, 0, count);
        }
        operationToken.ThrowIfCancellationRequested();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                root.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != root.EnumerateObject().Count())
                throw new System.Text.Json.JsonException();
            var receipt = System.Text.Json.JsonSerializer.Deserialize<PluginImportResumeReceipt>(root.GetRawText(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (receipt is null || receipt.SetupId != setupId || receipt.CorrelationId == Guid.Empty || receipt.Revision <= expectedRevision)
                throw new System.Text.Json.JsonException();
            return receipt;
        }
        catch (System.Text.Json.JsonException) { throw new InvalidOperationException("The import acknowledgment is unavailable."); }
    }
    public async Task SaveGrantAsync(Guid id, Guid accountId, SavePluginAccountGrantRequest request, CancellationToken ct = default) => await Send<object>(HttpMethod.Put, Path(id) + $"/{accountId:D}/business-access", request, ct);
    public async Task RevokeGrantAsync(Guid id, Guid grantId, long revision, CancellationToken ct = default) => await Send<object>(HttpMethod.Delete, Path(id) + $"/business-access/{grantId:D}?revision={revision}", null, ct);
    public Task<PluginAccountImpactView> GetDisconnectImpactAsync(Guid id, Guid accountId, CancellationToken ct = default) => Send<PluginAccountImpactView>(HttpMethod.Get, Path(id) + $"/{accountId:D}/disconnect-impact", null, ct);
    public async Task DisconnectAsync(Guid id, Guid accountId, DisconnectPluginAccountRequest request, CancellationToken ct = default) => await Send<object>(HttpMethod.Post, Path(id) + $"/{accountId:D}/disconnect", request, ct);
    public async Task SaveOperatorAsync(Guid id,SavePluginOperatorRequest request,CancellationToken ct=default)=>await Send<object>(HttpMethod.Put,Path(id)+"/operators",request,ct);
    public async Task RevokeOperatorAsync(Guid id,Guid operatorId,long revision,CancellationToken ct=default)=>await Send<object>(HttpMethod.Delete,Path(id)+$"/operators/{operatorId:D}?revision={revision}",null,ct);
    public Task<PluginInstallationImpact> GetImpactAsync(Guid id,CancellationToken ct=default)=>Send<PluginInstallationImpact>(HttpMethod.Get,Path(id)+"/impact",null,ct);
    public async Task ControlAsync(Guid id,PluginInstallationControlRequest request,CancellationToken ct=default)=>await Send<object>(HttpMethod.Post,Path(id)+"/control",request,ct);
    public async Task RetryBuildAsync(Guid id,CancellationToken ct=default)=>await Send<object>(HttpMethod.Post,Path(id)+"/retry-build",null,ct);
    public Task<PluginPackageReview> GetReviewAsync(Guid id,CancellationToken ct=default)=>Send<PluginPackageReview>(HttpMethod.Get,Path(id)+"/review",null,ct);
    private async Task<T> SendUpgrade<T>(HttpMethod method,string url,object? body,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var budget=http.Timeout==Timeout.InfiniteTimeSpan || http.Timeout>TimeSpan.FromSeconds(30)?TimeSpan.FromSeconds(30):http.Timeout;
        deadline.CancelAfter(budget);var operation=deadline.Token;
        using var request=new HttpRequestMessage(method,url){Content=body is null?null:JsonContent.Create(body)};
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,operation);
        if(!response.IsSuccessStatusCode)
        {
            // A proxy or another server cannot inject arbitrary diagnostic text or a body into this control.
            var message=response.StatusCode switch
            {
                System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized=>"Use the original consenting administrator to review this connection upgrade.",
                System.Net.HttpStatusCode.BadRequest=>"This connection is not eligible for the compatible upgrade. Refresh its account and package review.",
                System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NotImplemented=>"This server does not support compatible connection upgrades.",
                System.Net.HttpStatusCode.Conflict=>"The account changed concurrently. Check upgrade status before continuing.",
                _=>"The upgrade outcome is unconfirmed. Check upgrade status before continuing."
            };
            throw new ApiClientException(response.StatusCode,message);
        }
        await using var stream=await response.Content.ReadAsStreamAsync(operation).WaitAsync(operation);
        using var bytes=new MemoryStream();var buffer=new byte[2048];
        while(true)
        {var count=await stream.ReadAsync(buffer,operation).AsTask().WaitAsync(operation);if(count==0)break;
            if(bytes.Length+count>32768)throw new InvalidOperationException("The upgrade response is unavailable. Check upgrade status before continuing.");bytes.Write(buffer,0,count);}
        try{return System.Text.Json.JsonSerializer.Deserialize<T>(bytes.ToArray(),new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            ??throw new System.Text.Json.JsonException();}
        catch(System.Text.Json.JsonException){throw new InvalidOperationException("The upgrade response is unavailable. Check upgrade status before continuing.");}
    }
    private async Task<T> Send<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
                throw new ApiClientException(response.StatusCode, "Sign in with an account authorized to manage this connection, or ask an enterprise administrator for access.");
            Error? error = null;
            try { error = await response.Content.ReadFromJsonAsync<Error>(ct); }
            catch (System.Text.Json.JsonException) { /* Never render an HTML gateway error or raw server response. */ }
            throw new ApiClientException(response.StatusCode, error?.ErrorMessage ?? "Account management is unavailable. Please refresh and try again.");
        }
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return default!;
        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new InvalidOperationException("The account response was empty.");
    }
    private sealed record Error([property: System.Text.Json.Serialization.JsonPropertyName("error")] string ErrorMessage);
}
