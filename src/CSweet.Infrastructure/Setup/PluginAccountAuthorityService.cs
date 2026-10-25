using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Frozen independent authorities. A provider token or whole-account business grant cannot create a consumer binding.</summary>
public sealed record PluginAccountAuthoritySnapshot(Guid AccountGroupId, long AccountRevision,
    Guid ResourceId, long ResourceRevision, Guid ConnectionId, long AuthorizationRevision,
    long ResourceAuthorizationRevision, Guid BusinessGrantId, long BusinessGrantRevision,
    Guid ConsumerBindingId, string GrantPayloadHash, string AccountIdentityHash);

/// <summary>Host-only broker boundary. Does not create grants and accepts no caller-selected account context.</summary>
public sealed class PluginAccountAuthorityService(CSweetDbContext db, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<(PluginAccountAuthoritySnapshot Snapshot, PluginConnection Connection, string ExternalResourceId)> RequireAsync(
        Guid organizationId, AgentCapabilityBinding binding, AgentInstallation connector, PluginManifest manifest, CancellationToken token)
    {
        if (manifest.Accounts is not { } declaration || connector.Scope != PluginInstallationScope.System ||
            binding.ProviderInstallationId != connector.Id || binding.OrganizationId != organizationId.ToString("D") ||
            binding.RevokedAt is not null || binding.PluginAccountBusinessGrantId is not { } grantId ||
            binding.PluginAccountResourceId is not { } resourceId || binding.PluginAccountConnectionId is not { } connectionId)
            throw Denied();
        var grant = await db.PluginAccountBusinessGrants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == grantId &&
            x.PluginInstallationId == connector.Id && x.OrganizationId == organizationId && x.RevokedAt == null, token) ?? throw Denied();
        var group = await db.PluginAccountGroups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == grant.AccountGroupId &&
            x.PluginInstallationId == connector.Id && x.State == PluginAccountState.Connected, token) ?? throw Denied();
        var resource = await db.PluginAccountResources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == resourceId &&
            x.AccountGroupId == group.Id && x.PluginInstallationId == connector.Id && x.IsAvailable, token) ?? throw Denied();
        var connection = await db.PluginConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == connectionId &&
            x.AccountGroupId == group.Id && x.AgentInstallationId == connector.Id &&
            x.DeclarationId == declaration.Connection && x.Status == PluginConnectionStatus.Connected, token) ?? throw Denied();
        var proof = await db.PluginAccountResourceAuthorizations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ResourceId == resource.Id && x.ConnectionId == connection.Id && x.IsAvailable, token) ?? throw Denied();
        var selected = Parse<Guid>(grant.SelectedResourceIdsJson);
        var capabilities = Parse<string>(grant.CapabilitiesJson);
        var now = _clock.GetUtcNow();
        var identityEndpoint = manifest.ProviderOperations.FirstOrDefault(x => x.Capability == declaration.Identity?.Capability)?.Http?.Endpoint;
        if (grant.ApprovedByApplicationUserId == Guid.Empty || grant.Revision < 1 || group.Revision < 1 ||
            resource.Revision < 1 || connection.AuthorizationRevision < 1 || proof.Revision < 1 ||
            grant.PackageDigest != connector.PackageVersion!.PackageDigest || grant.PolicyVersion != declaration.PolicyVersion ||
            !capabilities.Contains(binding.Capability, StringComparer.Ordinal) ||
            !declaration.Permissions.Any(x => x.Capabilities.Contains(binding.Capability, StringComparer.Ordinal)) ||
            resource.ResourceType != declaration.ResourceType || string.IsNullOrWhiteSpace(resource.ExternalResourceId) ||
            connection.ProviderProfile != group.ProviderProfile ||
            group.IdentityKind is not (PluginAccountIdentityKind.Administrative or PluginAccountIdentityKind.ProviderSubject) ||
            group.IdentityKind == PluginAccountIdentityKind.Administrative && (group.VerifiedSubject is not null || group.IdentityAuthority is not null) ||
            group.IdentityKind == PluginAccountIdentityKind.ProviderSubject && (declaration.Identity is null ||
                string.IsNullOrWhiteSpace(group.VerifiedSubject) || group.IdentityAuthority != identityEndpoint ||
                connection.ExternalAccountId != group.VerifiedSubject) ||
            connection.BoundResourceId is { } bound && bound != resource.ExternalResourceId ||
            !Current(resource.VerifiedAt, declaration.Lifecycle.MetadataRefreshDays, now) ||
            !Current(proof.VerifiedAt, declaration.Lifecycle.MetadataRefreshDays, now) ||
            grant.Scope == PluginAccountGrantScope.SelectedResources && !selected.Contains(resource.Id) ||
            grant.Scope == PluginAccountGrantScope.WholeAccount && (selected.Length != 0 || grant.FutureResourcesAcknowledgedAt is null || grant.FutureResourcesAcknowledgedAt > now) ||
            grant.Scope is not (PluginAccountGrantScope.SelectedResources or PluginAccountGrantScope.WholeAccount))
            throw Denied();
        var payload = JsonSerializer.SerializeToElement(new { grant.Scope, selected, capabilities, grant.PackageDigest,
            grant.PolicyVersion, grant.ApprovedByApplicationUserId, grant.FutureResourcesAcknowledgedAt });
        var identity = JsonSerializer.SerializeToElement(new { group.IdentityKind, group.VerifiedSubject, group.IdentityAuthority, group.ProviderProfile });
        return (new(group.Id, group.Revision, resource.Id, resource.Revision, connection.Id, connection.AuthorizationRevision,
            proof.Revision, grant.Id, grant.Revision, binding.Id, ConnectorRequestMaterializer.Hash(payload),
            ConnectorRequestMaterializer.Hash(identity)), connection, resource.ExternalResourceId);
    }

    private static bool Current(DateTimeOffset verifiedAt, int days, DateTimeOffset now) =>
        verifiedAt <= now && verifiedAt > now.AddDays(-days);
    private static T[] Parse<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T[]>(json) ?? throw Denied(); }
        catch (JsonException) { throw Denied(); }
    }
    private static UnauthorizedAccessException Denied() => new("The selected account resource is not currently authorized for this business and consumer.");
}
