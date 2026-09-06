using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Bounded read-only slice of the connector execution protocol. Mutations cannot enter this path.</summary>
public sealed class ConnectorReadExecutor(CSweetDbContext db, ConnectorPlanService plans, IConnectorHttpTransport transport,
    IPluginSecretStore secrets, IAuditEventWriter audit)
{
    public async Task<JsonElement> ExecuteAsync(Guid organizationId, Guid requesterId, string capability,
        JsonElement input, string idempotencyKey, CancellationToken token)
    {
        var execution = await plans.PrepareAsync(organizationId, requesterId, capability, input, idempotencyKey, token);
        var prepared = JsonSerializer.Deserialize<FrozenConnectorPlan>(execution.PlanJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new UnauthorizedAccessException("The frozen request is unavailable.");
        if (prepared.Request.Effect != "read" || prepared.Request.Method != "GET" || prepared.Request.MediaAssetId is not null)
            throw new UnauthorizedAccessException("Mutations and media transfers require the approved-action executor.");
        if (execution.Status == "Completed" && execution.ResultJson is { } stored)
            return JsonDocument.Parse(stored).RootElement.Clone();
        var frozen = await plans.RevalidateAsync(organizationId, requesterId, execution.Id, execution.PlanHash, token);
        if (frozen.Request.Effect != "read" || frozen.Request.Method != "GET" || frozen.Request.MediaAssetId is not null)
            throw new UnauthorizedAccessException("Mutations and media transfers require the approved-action executor.");
        if (execution.Status == "Executing" && execution.UpdatedAt > DateTimeOffset.UtcNow.AddSeconds(-45))
            throw new InvalidOperationException("This read is already in progress.");
        execution.Status = "Executing"; execution.Revision++; execution.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token); // Optimistic claim: competing contexts cannot claim the same revision.
        async Task Revalidate(CancellationToken cancellation) =>
            _ = await plans.RevalidateAsync(organizationId, requesterId, execution.Id, execution.PlanHash, cancellation);
        foreach (var resource in frozen.Request.ResourceChecks)
        {
            var check = resource.Declaration;
            var query = check.QueryConstants.Append(new KeyValuePair<string, string>(check.ResourceQuery, resource.ResourceId));
            var ownershipRequest = frozen.Request with
            {
                Method = "GET", Url = ConnectorRequestMaterializer.Query(check.Endpoint, query), Body = null,
                ResourceChecks = [], SecretResponseFields = []
            };
            var ownership = await transport.SendAsync(frozen.ConnectorInstallationId, frozen.ConnectionId,
                ownershipRequest, Revalidate, token);
            if (ownership.StatusCode != 200) throw new UnauthorizedAccessException("The provider could not validate resource ownership.");
            using var document = Parse(ownership.Body);
            var owner = ConnectorRequestMaterializer.At(document.RootElement, check.OwnerPointer);
            if (owner is not { ValueKind: JsonValueKind.String } || owner.Value.GetString() != frozen.ResourceId)
                throw new UnauthorizedAccessException("The provider resource does not belong to the confirmed account.");
        }
        var response = await transport.SendAsync(frozen.ConnectorInstallationId, frozen.ConnectionId, frozen.Request, Revalidate, token);
        if (response.StatusCode is < 200 or >= 300)
            throw new InvalidOperationException($"The provider read failed with status {response.StatusCode}; no response content was released.");
        var sanitized = await SecretResponseSanitizer.SanitizeAsync(response.Body, frozen.Request.SecretResponseFields,
            async (pointer, value, cancellation) =>
            {
                var referenceId = Guid.NewGuid().ToString("N"); var key = $"response.{referenceId}";
                // Register the vault key before writing so failed reads cannot create untracked secrets.
                db.PluginOperationalStates.Add(new() { Id = Guid.NewGuid(), OrganizationId = organizationId,
                    AgentInstallationId = frozen.ConnectorInstallationId, Kind = "response-secret-reference", ExternalKey = referenceId,
                    PayloadJson = JsonSerializer.Serialize(new { key, connectionId = frozen.ConnectionId, pointer }),
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Revision = 1 });
                await db.SaveChangesAsync(cancellation);
                await secrets.SetAsync(frozen.ConnectorInstallationId, key, value, cancellation);
                return $"plugin-secret:{referenceId}";
            }, token);
        using var result = Parse(sanitized);
        var manifestJson = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == frozen.ConnectorInstallationId)
            .Select(x => x.PackageVersion!.ManifestJson).SingleAsync(token);
        var manifest = JsonSerializer.Deserialize<CSweet.Contracts.Plugins.PluginManifest>(manifestJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        RequestSchemaValidator.Validate(result.RootElement, manifest.Provides.Single(x => x.Name == capability).OutputSchema);
        await Revalidate(token);
        execution.ResultJson = ConnectorRequestMaterializer.Canonical(result.RootElement);
        execution.Status = "Completed"; execution.Revision++; execution.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
        await audit.WriteAsync("connector.read.completed", nameof(ConnectorExecution), execution.Id,
            $"Completed approved connector read {capability}.", null, token);
        return result.RootElement.Clone();
    }

    private static JsonDocument Parse(byte[] body)
    {
        try
        {
            var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            try { _ = ConnectorRequestMaterializer.Hash(document.RootElement); return document; }
            catch { document.Dispose(); throw; }
        }
        catch (JsonException) { throw new InvalidOperationException("The provider returned an invalid JSON response; it was withheld."); }
    }
}
