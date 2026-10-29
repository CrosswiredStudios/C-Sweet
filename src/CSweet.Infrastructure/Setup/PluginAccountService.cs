using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace CSweet.Infrastructure.Setup;

/// <summary>Native enterprise account management. Provider behavior comes only from reviewed declarations.</summary>
public sealed partial class PluginAccountService(CSweetDbContext db, IPluginSecretStore secrets,
    IDataProtectionProvider protection, IHttpClientFactory http, IPluginProviderProfileRegistry profiles,
    IConnectorHttpTransport transport, IOptions<PluginConnectionOptions> options, IAuditEventWriter audit,
    IEnumerable<IPluginAccountDataDeletionParticipant>? deletionParticipants = null,IHostEnvironment? environment = null,
    IManagedPluginConnectionGateway? managed = null, ConnectionServiceSelectionStore? selections = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _state = protection.CreateProtector("CSweet.PluginAccountOAuth.State.v1");
    private PluginAccountAccessService Access => new(db);
    private ManagedPluginConnectionOptions ManagedSettings => selections?.Effective(options.Value.Managed) ?? options.Value.Managed;
    private bool DevelopmentPreview=>options.Value.AllowDevelopmentLifecyclePreview && environment?.IsDevelopment()==true;
    private string[] MissingDeletionStores=>new[]{"external:backups","external:processors","external:memory","external:runtime","external:retained-evidence"}
        .Except((deletionParticipants ?? []).Where(p=>p.IsConfigured).Select(p=>p.Store),StringComparer.Ordinal).ToArray();

    private async Task<(AgentInstallation Installation, PluginManifest Manifest, PluginConnectionDeclaration Connection)> Context(Guid id, CancellationToken ct, bool management = false)
    {
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).Include(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == id && x.Scope == PluginInstallationScope.System &&
                (management || x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active) && x.PackageVersion!.PluginKind == PluginKind.Connector, ct)
            ?? throw new UnauthorizedAccessException("The enterprise integration is unavailable.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion!.ManifestJson, Json)!;
        PluginManifestReader.ValidateConnectorContracts(manifest);
        if (manifest.Accounts is null || installation.Grant is null) throw new InvalidOperationException("This plugin does not declare native account management.");
        return (installation, manifest, manifest.Connections.Single(x => x.Id == manifest.Accounts.Connection));
    }

    public async Task<PluginAccountManagementView> GetAsync(Guid userId, Guid installationId, CancellationToken ct)
    {
        var (installation, manifest, declaration) = await Context(installationId, ct, management: true);
        if (!await Access.IsActiveHumanAsync(userId, ct)) throw new UnauthorizedAccessException("Your account cannot manage this connection.");
        var admin = await Access.IsAdministratorAsync(userId, ct);
        var operators = await db.PluginAccountOperators.AsNoTracking().Where(x => x.PluginInstallationId == installationId && x.ApplicationUserId == userId && x.RevokedAt == null).ToArrayAsync(ct);
        if (!admin && operators.Length == 0) throw new UnauthorizedAccessException("Ask an administrator for account connection access.");
        var build = installation.PackageVersion!.Status.ToString();
        var canConnect = installation.IsEnabled && installation.RevisionStatus == PluginRevisionStatus.Active && build == "Built" && !string.IsNullOrWhiteSpace(installation.PackageVersion.PackageDigest);
        string? readiness = canConnect ? null : "build_pending";
        var managedMode = managed?.UsesManaged(declaration.ProviderProfile) == true;
        if (canConnect && managedMode)
        {
            try
            {
                var availability = await managed!.Availability(installation.PackageVersion.PackageDigest!, ct);
                if (availability.ProfileId != declaration.ProviderProfile) throw new InvalidOperationException("The managed application binding differs from this package.");
            }
            catch (Exception error) when (error is HttpRequestException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
            { readiness = "managed_connection_unavailable"; canConnect = false; }
        }
        var profile = canConnect && !managedMode ? await profiles.ResolveAsync(declaration.ProviderProfile, ct) : null;
        if (canConnect && !managedMode && profile is null)
        { readiness = "provider_configuration_required"; canConnect = false; }
        else if (canConnect && !managedMode)
        {
            var p = profile;
            if (p!.AuthorizationEndpoint != declaration.Provider!.AuthorizationEndpoint || p.TokenEndpoint != declaration.Provider.TokenEndpoint ||
                p.RevocationEndpoint != declaration.Provider.RevocationEndpoint)
            { readiness = "provider_configuration_mismatch"; canConnect = false; }
        }
        string? redirectUri = null;
        try
        {
            if (!managedMode)
            {
                redirectUri = PluginConnectionAddresses.Callback(options.Value);
                _ = PluginConnectionAddresses.Return(options.Value, "/settings/plugins", environment?.IsDevelopment() == true);
            }
        }
        catch (InvalidOperationException)
        {
            if (canConnect) { readiness = "callback_configuration_required"; canConnect = false; }
        }
        var groups = await db.PluginAccountGroups.AsNoTracking().Where(x => x.PluginInstallationId == installationId &&
            x.State != PluginAccountState.Disconnected).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        groups = groups.Where(g => (admin || operators.Any(o => o.AccountGroupId is null || o.AccountGroupId == g.Id)) &&
            (g.State != PluginAccountState.ConnectionRequired || db.PluginConnections.Any(c => c.AccountGroupId == g.Id && c.Status != PluginConnectionStatus.Pending && c.Status != PluginConnectionStatus.Revoked))).ToList();
        if (canConnect && !admin && !operators.Any(o => o.AccountGroupId is null))
        { canConnect = false; readiness = "assigned_accounts_only"; }
        var rows = new List<PluginManagedAccount>();
        foreach (var group in groups)
        {
            var resources = await db.PluginAccountResources.AsNoTracking().Where(x => x.AccountGroupId == group.Id && x.PluginInstallationId == installationId)
                .OrderBy(x => x.DisplayName).Select(x => new PluginManagedResource(x.Id, x.ExternalResourceId, x.DisplayName, x.Handle, x.IsAvailable)).ToArrayAsync(ct);
            var authorizations = await db.PluginConnections.AsNoTracking().Where(x => x.AccountGroupId == group.Id && x.AgentInstallationId == installationId &&
                x.Status != PluginConnectionStatus.Pending && x.Status != PluginConnectionStatus.Revoked).ToArrayAsync(ct);
            var grants = admin ? await db.PluginAccountBusinessGrants.AsNoTracking().Where(x => x.AccountGroupId == group.Id).ToArrayAsync(ct) : [];
            var authorizationIds = authorizations.Select(x => x.Id).ToArray();
            var proofs = await db.PluginAccountResourceAuthorizations.AsNoTracking().Where(x => authorizationIds.Contains(x.ConnectionId)).ToArrayAsync(ct);
            rows.Add(new(group.Id, group.DisplayName, group.State.ToString(), group.Revision, group.IdentityKind == PluginAccountIdentityKind.ProviderSubject,
                resources, authorizations.Select(c => new PluginManagedAuthorization(c.Id, c.Status.ToString(),
                    declaration.ScopeSets.Where(s => !s.Scopes.Except(List<string>(c.GrantedScopesJson), StringComparer.Ordinal).Any()).Select(s => s.Id).ToArray(), group.State == PluginAccountState.Connected && installation.IsEnabled && build=="Built" && readiness is null or "assigned_accounts_only")
                    { ResourceIds = proofs.Where(p => p.ConnectionId == c.Id).Select(p => p.ResourceId).ToArray() }).ToArray(),
                grants.Select(g => new PluginBusinessAccountGrantView(g.Id, g.OrganizationId, g.Scope.ToString(), List<Guid>(g.SelectedResourceIdsJson),
                    manifest.Accounts!.Permissions.Where(p => p.Capabilities.All(c => List<string>(g.CapabilitiesJson).Contains(c))).Select(p => p.Id).ToArray(), g.Revision, g.RevokedAt != null)).ToArray()));
        }
        if (!installation.IsEnabled) readiness = "plugin_paused";
        if(installation.RevisionStatus==PluginRevisionStatus.Staged) readiness="update_review_required";
        var sessions = await db.PluginAccountSetupSessions.AsNoTracking().Where(x => x.PluginInstallationId == installationId && !x.IsMaintenance &&
            (admin || x.ApplicationUserId == userId)).OrderByDescending(x => x.CreatedAt).Take(20).ToArrayAsync(ct);
        return new(installationId, manifest.Name, manifest.Version, installation.PackageVersion.PackageDigest ?? "", build, canConnect, readiness,
            manifest.Accounts!, declaration.ScopeSets.Where(s => s.Required || manifest.Accounts!.Permissions.Any(p => p.ScopeSets.Contains(s.Id))).ToArray(), rows, sessions.Select(s => new PluginAccountSetupView(s.Id,
                s.Status == "AwaitingConsent" && s.ExpiresAt <= DateTimeOffset.UtcNow ? "Expired" : s.Status,
                List<ConnectorAccountChoice>(s.ObservedResourcesJson).Length, s.FailureCode, s.ExpiresAt)).ToArray(), admin)
        { PluginId = manifest.Id, OAuthRedirectUri = redirectUri, ConnectionMode = managedMode ? "Managed" : "CustomerOwned",
          ManagedServiceOrigin = managedMode && ManagedServiceTrust.Identity(ManagedSettings.ServiceOrigin) != "Unconfigured"
              ? ManagedServiceTrust.CanonicalOrigin(ManagedSettings.ServiceOrigin) : null,
          Administration = admin ? await Administration(installation, ct) : null,
          Deletions = admin ? await DeletionViews(installationId,ct) : [],
          CleanupReviews=await CleanupReviews(userId,installationId,ct,groups.Select(g=>g.Id).ToArray()) };
    }

    private static T[] List<T>(string json) => JsonSerializer.Deserialize<T[]>(json, Json) ?? [];
    private static string TokenKey(PluginAccountSetupSession s) => PluginOAuthCredentialKeys.Generation(s.ConnectionId, s.Id);
    private static string VerifierKey(Guid id) => $"oauth.account-setup.{id:N}.verifier";
    private static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
