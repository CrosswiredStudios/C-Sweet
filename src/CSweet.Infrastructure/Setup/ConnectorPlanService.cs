using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed record ConnectorMediaBinding(Guid AssetId, string Sha256, long SizeBytes, string ContentType);
public sealed record FrozenConnectorPlan(Guid OrganizationId, Guid RequesterInstallationId, Guid ConnectorInstallationId,
    Guid ConnectionId, long GrantRevision, long ProviderGrantRevision, string PackageDigest, string Capability,
    string ResourceId, string IdempotencyKey, string InputHash, ConnectorPreparedRequest Request,
    ConnectorMediaBinding? Media);

/// <summary>Freezes authority and request content before any provider effect. This service never sends HTTP.</summary>
public sealed class ConnectorPlanService(CSweetDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> IsAvailableAsync(Guid organizationId, Guid requesterId, string capability, CancellationToken token)
    {
        try { _ = await RequireAuthorityAsync(organizationId, requesterId, capability, token); return true; }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException or JsonException)
        { return false; }
    }

    public async Task<ConnectorExecution> PrepareAsync(Guid organizationId, Guid requesterId, string capability,
        JsonElement input, string idempotencyKey, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 160 || idempotencyKey.Any(char.IsControl))
            throw new ArgumentException("A bounded, stable idempotency key is required.");
        var authority = await RequireAuthorityAsync(organizationId, requesterId, capability, token);
        RequestSchemaValidator.ValidateSchema(authority.Operation.InputSchema);
        RequestSchemaValidator.Validate(input, authority.Operation.InputSchema);
        var inputHash = ConnectorRequestMaterializer.Hash(input);
        var request = ConnectorRequestMaterializer.Prepare(authority.Operation, input, authority.Connection.BoundResourceId!);
        ConnectorMediaBinding? media = null;
        if (request.MediaAssetId is { } asset)
        {
            var mediaAsset = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == Guid.Parse(asset) && x.OrganizationId == organizationId, token)
                ?? throw new UnauthorizedAccessException("The media asset is not in this organization.");
            if (mediaAsset.Sha256.Length != 64 || mediaAsset.SizeBytes <= 0)
                throw new InvalidOperationException("Media requires a completed, checksummed upload.");
            media = new(mediaAsset.Id, mediaAsset.Sha256, mediaAsset.SizeBytes, mediaAsset.ContentType);
        }
        var frozen = new FrozenConnectorPlan(organizationId, requesterId, authority.Connector.Id,
            authority.Connection.Id, authority.Requester.Grant!.GrantRevision, authority.Connector.Grant!.GrantRevision,
            authority.Connector.PackageVersion!.PackageDigest!, capability, authority.Connection.BoundResourceId!,
            idempotencyKey, inputHash, request, media);
        var element = JsonSerializer.SerializeToElement(frozen, JsonOptions);
        var hash = ConnectorRequestMaterializer.Hash(element);
        var existing = await db.ConnectorExecutions.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.RequesterInstallationId == requesterId && x.ConnectorInstallationId == authority.Connector.Id &&
            x.Capability == capability && x.IdempotencyKey == idempotencyKey, token);
        if (existing is not null)
        {
            if (existing.PlanHash != hash) throw new InvalidOperationException("This idempotency key belongs to a different plan or authority revision.");
            using var storedPlan = JsonDocument.Parse(existing.PlanJson);
            if (ConnectorRequestMaterializer.Hash(storedPlan.RootElement) != hash)
                throw new UnauthorizedAccessException("The stored request plan was modified.");
            return existing;
        }
        var now = DateTimeOffset.UtcNow;
        var execution = new ConnectorExecution
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, RequesterInstallationId = requesterId,
            ConnectorInstallationId = authority.Connector.Id, ConnectionId = authority.Connection.Id,
            GrantRevision = frozen.GrantRevision, PackageDigest = frozen.PackageDigest, Capability = capability,
            ResourceId = frozen.ResourceId, IdempotencyKey = idempotencyKey, InputHash = inputHash, PlanHash = hash,
            PlanJson = ConnectorRequestMaterializer.Canonical(element), CreatedAt = now, UpdatedAt = now,
            ExpiresAt = now.AddHours(24), Revision = 1
        };
        db.ConnectorExecutions.Add(execution);
        await db.SaveChangesAsync(token);
        return execution;
    }

    /// <summary>Must be called immediately before each request/chunk by the credential broker.</summary>
    public async Task<FrozenConnectorPlan> RevalidateAsync(Guid organizationId, Guid requesterId, Guid planId,
        string expectedHash, CancellationToken token)
    {
        var execution = await db.ConnectorExecutions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == planId && x.OrganizationId == organizationId && x.RequesterInstallationId == requesterId, token)
            ?? throw new UnauthorizedAccessException("The plan is not owned by this installation.");
        if (execution.PlanHash != expectedHash || execution.ExpiresAt <= DateTimeOffset.UtcNow ||
            execution.Status is "Cancelled" or "Indeterminate" or "Completed" or "Failed")
            throw new InvalidOperationException("The plan is stale or no longer executable.");
        var element = JsonDocument.Parse(execution.PlanJson).RootElement;
        if (ConnectorRequestMaterializer.Hash(element) != expectedHash)
            throw new InvalidOperationException("The stored plan was modified.");
        var frozen = element.Deserialize<FrozenConnectorPlan>(JsonOptions)!;
        var authority = await RequireAuthorityAsync(organizationId, requesterId, execution.Capability, token);
        if (frozen.OrganizationId != organizationId || frozen.RequesterInstallationId != requesterId ||
            frozen.ConnectorInstallationId != authority.Connector.Id || frozen.ConnectionId != authority.Connection.Id ||
            frozen.GrantRevision != authority.Requester.Grant!.GrantRevision ||
            frozen.ProviderGrantRevision != authority.Connector.Grant!.GrantRevision ||
            frozen.PackageDigest != authority.Connector.PackageVersion!.PackageDigest ||
            frozen.ResourceId != authority.Connection.BoundResourceId || frozen.Capability != execution.Capability ||
            frozen.IdempotencyKey != execution.IdempotencyKey || frozen.InputHash != execution.InputHash)
            throw new UnauthorizedAccessException("Plan authority has changed; prepare and approve a new plan.");
        if (frozen.Media is { } media && !await db.MediaAssets.AsNoTracking().AnyAsync(x => x.Id == media.AssetId &&
            x.OrganizationId == organizationId && x.Sha256 == media.Sha256 && x.SizeBytes == media.SizeBytes &&
            x.ContentType == media.ContentType, token))
            throw new UnauthorizedAccessException("The approved media asset has changed or was removed.");
        return frozen;
    }

    private async Task<Authority> RequireAuthorityAsync(Guid organizationId, Guid requesterId, string capability, CancellationToken token)
    {
        var organization = organizationId.ToString("D");
        var requester = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant).Include(x => x.PackageVersion)
            .SingleOrDefaultAsync(x => x.Id == requesterId && x.BusinessId == organization && x.IsEnabled &&
                x.SetupState == PluginSetupState.Ready && x.RevisionStatus == PluginRevisionStatus.Active, token)
            ?? throw new UnauthorizedAccessException("The consuming installation is not active in this organization.");
        if (requester.Grant is null || !List(requester.Grant.RequiredCapabilitiesJson).Contains(capability))
            throw new UnauthorizedAccessException("The consuming installation has not been granted this capability.");
        var manifest = Manifest(requester);
        var requirement = manifest.Requires.SingleOrDefault(x => x.Name == capability && x.Dependency != null)
            ?? throw new UnauthorizedAccessException("The capability has no declared connector dependency.");
        var dependency = manifest.Dependencies.Single(x => x.Id == requirement.Dependency);
        var binding = await db.AgentCapabilityBindings.AsNoTracking().Include(x => x.ProviderInstallation!).ThenInclude(x => x.PackageVersion)
            .Include(x => x.ProviderInstallation!).ThenInclude(x => x.Grant)
            .SingleOrDefaultAsync(x => x.OrganizationId == organization && x.RequesterInstallationId == requesterId &&
                x.Capability == capability && x.DependencyId == dependency.Id && x.GrantRevision == requester.Grant.GrantRevision &&
                x.RevokedAt == null, token) ?? throw new UnauthorizedAccessException("Select and authorize a connector account.");
        var connector = binding.ProviderInstallation ?? throw new UnauthorizedAccessException("The connector was removed.");
        ConnectorBindingService.ValidatePackage(dependency, connector.PackageVersion!);
        if (connector.BusinessId != organization || connector.Scope != PluginInstallationScope.Organization || !connector.IsEnabled ||
            connector.SetupState != PluginSetupState.Ready || connector.RevisionStatus != PluginRevisionStatus.Active ||
            binding.ProviderPackageDigest != connector.PackageVersion!.PackageDigest || connector.Grant is null ||
            !List(connector.Grant.ProvidedCapabilitiesJson).Contains(capability))
            throw new UnauthorizedAccessException("The connector or its reviewed grants have changed.");
        var provider = Manifest(connector);
        PluginManifestReader.ValidateConnectorContracts(provider);
        var operation = provider.ProviderOperations.Single(x => x.Capability == capability && x.Http != null);
        var declaration = provider.Connections.Single(x => x.Id == operation.Http!.Connection);
        var connection = await db.PluginConnections.AsNoTracking().SingleOrDefaultAsync(x =>
            x.AgentInstallationId == connector.Id && x.DeclarationId == declaration.Id && x.Status == PluginConnectionStatus.Connected, token)
            ?? throw new UnauthorizedAccessException("Reconnect the provider before doing channel work.");
        var scopes = List(connection.GrantedScopesJson);
        if (connection.ProviderProfile != declaration.ProviderProfile || string.IsNullOrWhiteSpace(connection.BoundResourceId) || operation.Http!.ScopeSets
            .SelectMany(id => declaration.ScopeSets.Single(x => x.Id == id).Scopes).Except(scopes, StringComparer.Ordinal).Any())
            throw new UnauthorizedAccessException("Confirm the account and complete the required provider consent.");
        if (!await db.ConnectorProfileApprovals.AsNoTracking().AnyAsync(x => x.ConnectorInstallationId == connector.Id &&
            x.PackageDigest == connector.PackageVersion!.PackageDigest && x.ProfileId == declaration.ProviderProfile &&
            x.RevokedAt == null, token)) throw new UnauthorizedAccessException("This build is not approved to use the provider profile.");
        return new(requester, connector, connection, operation);
    }

    private static string[] List(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];
    private static PluginManifest Manifest(AgentInstallation installation) =>
        JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion!.ManifestJson, JsonOptions)!;
    private sealed record Authority(AgentInstallation Requester, AgentInstallation Connector, PluginConnection Connection,
        PluginProviderOperationDeclaration Operation);
}
