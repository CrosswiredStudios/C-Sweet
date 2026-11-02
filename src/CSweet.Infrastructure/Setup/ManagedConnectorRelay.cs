using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.ConnectionService.Protocol;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public interface IManagedConnectorRelay
{
    Task<bool> IsManagedAsync(Guid connector, Guid connection, CancellationToken ct);
    Task<ConnectorProviderResponse> ReadAsync(FrozenConnectorPlan plan, string planHash, Func<CancellationToken, Task> revalidate, CancellationToken ct);
    Task<ConnectorMediaResponse> MediaAsync(FrozenConnectorPlan plan, ConnectorMediaStep step, string? session,
        long offset, ReadOnlyMemory<byte> chunk, long maximumSentBytes, Func<CancellationToken, Task> revalidate, CancellationToken ct);
    Task CancelAsync(Guid connector, Guid connection, string session, CancellationToken ct);
}

/// <summary>Core independently verifies current native account grants, frozen plans, media provenance
/// and action decisions. Only a signed attestation crosses to the service; provider tokens never do.</summary>
public sealed class ManagedConnectorRelay(CSweetDbContext db, IPluginSecretStore secrets, IManagedPluginConnectionGateway gateway,
    ConnectorPlanService plans, ConnectorActionApprovalService approvals) : IManagedConnectorRelay
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<bool> IsManagedAsync(Guid connector, Guid connection, CancellationToken ct)
    {
        var row = await db.PluginConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == connection && x.AgentInstallationId == connector, ct)
            ?? throw new UnauthorizedAccessException("The connection is unavailable.");
        var context = await secrets.GetAsync(connector, $"managed.context.{connection:N}", ct);
        if (context is not null) return true; // Never fall back to a local credential after a mode switch.
        if (gateway.UsesManaged(row.ProviderProfile)) throw new UnauthorizedAccessException("The managed connection context is unavailable.");
        return false;
    }
    public async Task<ConnectorProviderResponse> ReadAsync(FrozenConnectorPlan plan, string planHash, Func<CancellationToken, Task> revalidate, CancellationToken ct)
    {
        if (plan.Request.Effect != "read" || plan.Request.Method != "GET" || plan.Media is not null) throw new UnauthorizedAccessException();
        var execution = await Execution(plan, planHash, false, ct);
        await revalidate(ct); var context = await Context(plan.ConnectorInstallationId, plan.ConnectionId, ct);
        var request = Operation(plan, planHash); var result = await gateway.ReadOperation(context.RemoteConnectionId!.Value, request, context.ServiceOrigin!, ct);
        if (result.ConnectionId != context.RemoteConnectionId || result.OperationHash != ManagedExecutionBinding.Hash(context.RemoteConnectionId.Value, request))
            throw new UnauthorizedAccessException("The managed operation receipt changed.");
        await Execution(plan, execution.PlanHash, false, ct); await revalidate(ct);
        return new(200, JsonSerializer.SerializeToUtf8Bytes(result.Result));
    }
    public async Task<ConnectorMediaResponse> MediaAsync(FrozenConnectorPlan plan, ConnectorMediaStep step, string? session,
        long offset, ReadOnlyMemory<byte> chunk, long maximumSentBytes, Func<CancellationToken, Task> revalidate, CancellationToken ct)
    {
        var hash = ConnectorRequestMaterializer.Hash(JsonSerializer.SerializeToElement(plan, Json));
        var execution = await Execution(plan, hash, true, ct);
        if (plan.Media is not { } asset || plan.Request.MediaProtocol != ConnectorResumableProtocol.Name ||
            maximumSentBytes < 0 || maximumSentBytes > asset.SizeBytes) throw new UnauthorizedAccessException("The media plan is unavailable.");
        await revalidate(ct); var context = await Context(plan.ConnectorInstallationId, plan.ConnectionId, ct);
        var request = new ManagedMediaRequest(Operation(plan, hash), plan.IdempotencyKey, asset.AssetId, asset.Sha256,
            asset.SizeBytes, asset.ContentType, execution.ApprovalId!.Value.ToString("D"), execution.ExpiresAt.ToUnixTimeSeconds());
        var binding = request.BindingHash(context.RemoteConnectionId!.Value);
        ManagedMediaResult result;
        if (step == ConnectorMediaStep.Begin)
        {
            if (session is not null || offset != 0 || !chunk.IsEmpty) throw new UnauthorizedAccessException();
            result = await gateway.BeginMedia(context.RemoteConnectionId.Value, request, context.ServiceOrigin!, ct);
        }
        else
        {
            var handle = ManagedMediaHandle.Require(session ?? "");
            if (handle.BindingHash != binding || handle.ConnectorId != plan.ConnectorInstallationId || handle.ConnectionId != plan.ConnectionId ||
                handle.ServiceOrigin != context.ServiceOrigin) throw new UnauthorizedAccessException("The saved media session belongs to a different plan.");
            result = step switch
            {
                ConnectorMediaStep.Probe when offset == 0 && chunk.IsEmpty => await gateway.ProbeMedia(handle.TransferId, binding, context.ServiceOrigin!, ct),
                ConnectorMediaStep.Chunk when !chunk.IsEmpty && offset >= 0 && chunk.Length <= asset.SizeBytes - offset &&
                    maximumSentBytes >= offset + chunk.Length => await gateway.MediaChunk(handle.TransferId, binding, offset, chunk, context.ServiceOrigin!, ct),
                _ => throw new UnauthorizedAccessException("The media exchange changed its approved range.")
            };
            if (result.TransferId != handle.TransferId) throw new UnauthorizedAccessException();
        }
        if (result.BindingHash != binding || result.CommittedBytes > maximumSentBytes || result.State == "Completed" && maximumSentBytes != asset.SizeBytes)
            throw new UnauthorizedAccessException("The service acknowledged bytes outside this approved exchange.");
        await Execution(plan, hash, true, ct); await revalidate(ct);
        var retry = result.RetryAt is { } retryAt ? DateTimeOffset.FromUnixTimeSeconds(retryAt) : (DateTimeOffset?)null;
        return step == ConnectorMediaStep.Begin
            ? new(201, [], ManagedMediaHandle.Format(result.TransferId, binding, plan.ConnectorInstallationId, plan.ConnectionId, context.ServiceOrigin!), null, retry)
            : new(result.StatusCode, result.Result is { } value ? JsonSerializer.SerializeToUtf8Bytes(value) : [], null, result.CommittedBytes, retry);
    }
    public async Task CancelAsync(Guid connector, Guid connection, string session, CancellationToken ct)
    {
        // Revocation deliberately removes managed.context. The protected transfer handle retains
        // only the original cleanup destination/correlation, never provider or execution authority.
        var handle = ManagedMediaHandle.Require(session);
        if (handle.ConnectorId != connector || handle.ConnectionId != connection) throw new UnauthorizedAccessException();
        var receipt = await gateway.CancelMedia(handle.TransferId, handle.BindingHash, handle.ServiceOrigin, ct);
        if (receipt.TransferId != handle.TransferId || receipt.BindingHash != handle.BindingHash || receipt.State != "Cancelled") throw new UnauthorizedAccessException();
    }
    private async Task<ConnectorExecution> Execution(FrozenConnectorPlan plan, string hash, bool mutation, CancellationToken ct)
    {
        if (plan.OriginalInput is not { ValueKind: JsonValueKind.Object } input || ConnectorRequestMaterializer.Hash(input) != plan.InputHash ||
            ConnectorRequestMaterializer.Hash(JsonSerializer.SerializeToElement(plan, Json)) != hash)
            throw new UnauthorizedAccessException("Prepare a new plan with its original reviewed input.");
        var execution = await db.ConnectorExecutions.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == plan.OrganizationId &&
            x.RequesterInstallationId == plan.RequesterInstallationId && x.ConnectorInstallationId == plan.ConnectorInstallationId &&
            x.ConnectionId == plan.ConnectionId && x.PlanHash == hash && x.Status == "Executing", ct) ?? throw new UnauthorizedAccessException("The execution is not current.");
        var current = mutation ? await approvals.RequireApprovedAsync(plan.OrganizationId, plan.RequesterInstallationId, execution.Id, hash, ct)
            : await plans.RevalidateAsync(plan.OrganizationId, plan.RequesterInstallationId, execution.Id, hash, ct);
        if (ConnectorRequestMaterializer.Hash(JsonSerializer.SerializeToElement(current, Json)) != hash || mutation && execution.ApprovalId is null)
            throw new UnauthorizedAccessException();
        return execution;
    }
    private static ManagedReadRequest Operation(FrozenConnectorPlan plan, string hash) => new(plan.OrganizationId.ToString("D"),
        plan.ConnectorInstallationId, plan.RequesterInstallationId, plan.PackageDigest, plan.Capability, plan.ResourceId,
        plan.OriginalInput!.Value, hash, DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds());
    private async Task<ManagedAccountAttempt> Context(Guid connector, Guid connection, CancellationToken ct)
    {
        var json = await secrets.GetAsync(connector, $"managed.context.{connection:N}", ct) ?? throw new UnauthorizedAccessException("The managed context is unavailable.");
        var context = JsonSerializer.Deserialize<ManagedAccountAttempt>(json, Json) ?? throw new UnauthorizedAccessException();
        if (context.RemoteConnectionId is null || context.RemoteConnectionId == Guid.Empty || context.ServiceOrigin is null ||
            ManagedServiceTrust.CanonicalOrigin(context.ServiceOrigin) != context.ServiceOrigin ||
            await gateway.BoundServiceOrigin(ct) != context.ServiceOrigin) throw new UnauthorizedAccessException("The managed origin changed.");
        return context;
    }
}

internal sealed record ManagedMediaHandle(Guid TransferId, string BindingHash, Guid ConnectorId, Guid ConnectionId, string ServiceOrigin)
{
    private const string Prefix = "csweet-managed-media:v2:";
    internal static bool IsHandle(string value) => value.StartsWith("csweet-managed-media:", StringComparison.Ordinal);
    internal static string Format(Guid id, string binding, Guid connector, Guid connection, string origin)
    {
        var value = Prefix + id.ToString("N") + ":" + binding + ":" + connector.ToString("N") + ":" + connection.ToString("N") + ":" +
            Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(origin));
        _ = Require(value); return value;
    }
    internal static ManagedMediaHandle Require(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal) || value.Length > 2048) throw new UnauthorizedAccessException("A saved managed media handle is required.");
        var parts = value[Prefix.Length..].Split(':');
        if (parts.Length != 5 || !Guid.TryParseExact(parts[0], "N", out var id) || id == Guid.Empty || parts[1].Length != 64 ||
            !Guid.TryParseExact(parts[2], "N", out var connector) || connector == Guid.Empty ||
            !Guid.TryParseExact(parts[3], "N", out var connection) || connection == Guid.Empty ||
            parts[1].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new UnauthorizedAccessException("Invalid managed media handle.");
        try
        {
            var bytes = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(parts[4]);
            var origin = new System.Text.UTF8Encoding(false, true).GetString(bytes);
            if (ManagedServiceTrust.CanonicalOrigin(origin) != origin ||
                Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(bytes) != parts[4]) throw new InvalidOperationException();
            return new(id, parts[1], connector, connection, origin);
        }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidOperationException)
        { throw new UnauthorizedAccessException("Invalid managed media handle."); }
    }
    public override string ToString() => "Managed media handle [redacted]";
}
