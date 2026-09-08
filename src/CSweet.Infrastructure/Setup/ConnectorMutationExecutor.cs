using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Executes one explicitly approved, non-media request. Ambiguous outcomes never retry here.</summary>
public sealed class ConnectorMutationExecutor(CSweetDbContext db, ConnectorActionApprovalService approvals,
    IConnectorHttpTransport transport, IPluginSecretStore secrets, IAuditEventWriter audit)
{
    internal const string PreconditionFailureKind = "host-connector-precondition-failure";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task ExecuteAsync(Guid organizationId, Guid requesterId, Guid planId, string planHash, CancellationToken ct)
    {
        var frozen = await approvals.RequireApprovedAsync(organizationId, requesterId, planId, planHash, ct);
        if (frozen.Request.Effect == "read" || frozen.Request.Method == "GET" || frozen.Media is not null ||
            frozen.Request.MediaAssetId is not null)
            throw new UnauthorizedAccessException("This executor accepts only approved non-media mutations.");
        var execution = await db.ConnectorExecutions.SingleAsync(x => x.Id == planId, ct);
        // An earlier worker may have sent the request. Even an expired lease is not permission to resend.
        if (execution.Status != "Approved") throw new InvalidOperationException("This action already started; reconcile its result before further work.");
        execution.Status = "Executing"; execution.Revision++; execution.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var mayHaveReachedProvider = false;
        var preconditionFailed = false;
        async Task Revalidate(CancellationToken token) =>
            _ = await approvals.RequireApprovedAsync(organizationId, requesterId, planId, planHash, token);
        try
        {
            foreach (var resource in frozen.Request.ResourceChecks)
            {
                var check = resource.Declaration;
                var request = frozen.Request with { Method = "GET", Body = null, ResourceChecks = [], SecretResponseFields = [], IfMatch = null,
                    Url = ConnectorRequestMaterializer.Query(check.Endpoint,
                        check.QueryConstants.Append(new KeyValuePair<string, string>(check.ResourceQuery, resource.ResourceId))) };
                var ownership = await transport.SendAsync(frozen.ConnectorInstallationId, frozen.ConnectionId, request, Revalidate, ct);
                if (ownership.StatusCode != 200) throw new UnauthorizedAccessException("Resource ownership could not be verified.");
                using var document = Parse(ownership.Body);
                var owner = ConnectorRequestMaterializer.At(document.RootElement, check.OwnerPointer);
                if (owner is not { ValueKind: JsonValueKind.String } || owner.Value.GetString() != frozen.ResourceId)
                    throw new UnauthorizedAccessException("The requested resource belongs to another account.");
            }
            await Revalidate(ct);
            mayHaveReachedProvider = true; // Persisted Executing is the crash-recovery fence before dispatch.
            var response = await transport.SendAsync(frozen.ConnectorInstallationId, frozen.ConnectionId, frozen.Request, Revalidate, ct);
            if (response.StatusCode == 412 && frozen.Request.IfMatch is not null)
            {
                // A received failed conditional request is not an ambiguous lost response.
                preconditionFailed = true;
                mayHaveReachedProvider = false;
                throw new InvalidOperationException("The resource changed. Obtain fresh evidence and approval before another change.");
            }
            if (response.StatusCode is < 200 or >= 300)
                throw new InvalidOperationException("The provider did not confirm completion; reconcile before retrying.");
            ConnectorResponseResourceValidator.Validate(response.Body, frozen.Request);
            var sanitized = await SecretResponseSanitizer.SanitizeAsync(
                response.StatusCode == 204 && response.Body.Length == 0 ? "{}"u8.ToArray() : response.Body,
                frozen.Request.SecretResponseFields, async (pointer, value, token) =>
                {
                    var reference = Guid.NewGuid().ToString("N"); var key = $"response.{reference}";
                    await Revalidate(token);
                    db.PluginOperationalStates.Add(new() { Id = Guid.NewGuid(), OrganizationId = organizationId,
                        AgentInstallationId = frozen.ConnectorInstallationId, Kind = "response-secret-reference", ExternalKey = reference,
                        PayloadJson = JsonSerializer.Serialize(new { key, connectionId = frozen.ConnectionId, pointer }),
                        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Revision = 1 });
                    await db.SaveChangesAsync(token);
                    try
                    {
                        await secrets.SetAsync(frozen.ConnectorInstallationId, key, value, token);
                        await Revalidate(token);
                    }
                    catch
                    {
                        await secrets.RemoveAsync(frozen.ConnectorInstallationId, key, CancellationToken.None);
                        throw;
                    }
                    return $"plugin-secret:{reference}";
                }, ct);
            using var result = Parse(sanitized);
            var manifestJson = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == frozen.ConnectorInstallationId)
                .Select(x => x.PackageVersion!.ManifestJson).SingleAsync(ct);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(manifestJson, Json)!;
            RequestSchemaValidator.Validate(result.RootElement, manifest.Provides.Single(x => x.Name == frozen.Capability).OutputSchema);
            await Revalidate(ct);
            execution.ResultJson = ConnectorRequestMaterializer.Canonical(result.RootElement);
            execution.Status = "Completed"; execution.Revision++; execution.UpdatedAt = DateTimeOffset.UtcNow;
            await approvals.QueueExecutionEventAsync(execution, ct);
            await db.SaveChangesAsync(ct); // A caller never receives success before the sanitized result is durable.
        }
        catch
        {
            await db.Entry(execution).ReloadAsync(CancellationToken.None);
            if (execution.Status == "Executing")
            {
                execution.ResultJson = null;
                execution.Status = mayHaveReachedProvider ? "Indeterminate" : "Blocked";
                if (preconditionFailed)
                    db.PluginOperationalStates.Add(new() { Id = Guid.NewGuid(), OrganizationId = organizationId,
                        AgentInstallationId = requesterId, Kind = PreconditionFailureKind, ExternalKey = planId.ToString("N"),
                        PayloadJson = JsonSerializer.Serialize(new { planHash }), CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow, Revision = 1 });
                execution.Revision++; execution.UpdatedAt = DateTimeOffset.UtcNow;
                await approvals.QueueExecutionEventAsync(execution, CancellationToken.None);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            throw;
        }
        await audit.WriteAsync("connector.mutation.completed", nameof(ConnectorExecution), planId,
            "Persisted a sanitized result for the explicitly approved connector plan.", cancellationToken: ct);
    }

    private static JsonDocument Parse(byte[] body)
    {
        var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        try { _ = ConnectorRequestMaterializer.Hash(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
}
