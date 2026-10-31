using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.RemoteMcp;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
namespace CSweet.Infrastructure.Setup;

/// <summary>Native-only account administration and trusted consumer binding. Provider company identity is never inferred.</summary>
public sealed class RemoteMcpAccountService(CSweetDbContext db, IDataProtectionProvider protection)
{
    public const string BindingKind = "remote-mcp-binding.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal sealed record Selection(Guid Owner, Guid Connection, long Generation);
    private AccountVault Vault(AccountScope scope) => new(new RemoteMcpAccountRepository(db, scope), protection);
    private static AccountScope Scope(Guid owner, Guid business, Guid provider) => new(owner, business, provider);
    private static UnauthorizedAccessException Denied() => new("The connection or its approved business access is unavailable.");
    private static PluginManifest Manifest(AgentInstallation installation) => JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion!.ManifestJson, Json) ?? throw Denied();
    private static string[] Values(string? json) => JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? [];
    /// <summary>The broker policy comes only from the installed provider's reviewed manifest (pinned by package digest).</summary>
    internal static ReviewedRemoteMcpProfile Profile(AgentInstallation provider)
    {
        try { return ReviewedRemoteMcpProfile.FromManifestJson(provider.PackageVersion!.ManifestJson); }
        catch (InvalidOperationException) { throw Denied(); }
    }
    private async Task<AgentInstallation> Provider(Guid business, Guid provider, CancellationToken ct, bool management = false)
    {
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).Include(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == provider && x.BusinessId == business.ToString("D") && x.Scope == PluginInstallationScope.Organization &&
                (management || x.IsEnabled) && x.RevisionStatus == PluginRevisionStatus.Active && (management || x.SetupState == PluginSetupState.Ready), ct) ?? throw Denied();
        var manifest = Manifest(installation);
        if (manifest.Kind != "remote-mcp" || installation.PackageVersion!.Status != AgentPackageVersionStatus.Built || installation.Grant is null ||
            string.IsNullOrWhiteSpace(installation.PackageVersion.PackageDigest)) throw Denied();
        PluginManifestReader.ValidateConnectorContracts(manifest);
        return installation;
    }
    private static async Task RequireOwner(CSweetDbContext db, Guid owner, Guid business, CancellationToken ct)
    {
        if (!await new PluginAccountAccessService(db).IsActiveHumanAsync(owner, ct) ||
            !await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.ApplicationUserId == owner && x.OrganizationId == business &&
                x.IsActive && x.EmployeeType == EmployeeType.Human && x.PermissionLevel >= OrganizationPermissionLevel.Manager, ct)) throw Denied();
    }
    private async Task<AgentInstallation> Consumer(Guid business, Guid consumer, AgentInstallation provider, CancellationToken ct) =>
        (await ConsumerWithCapabilities(business, consumer, provider, ct)).Installation;

    /// <summary>
    /// The consumer's requirements for this dependency define what a grant covers. Every one must be reviewed in the
    /// provider manifest, approved in the consumer's installation grant and provided by the provider's grant.
    /// </summary>
    private async Task<(AgentInstallation Installation, string[] Capabilities)> ConsumerWithCapabilities(Guid business, Guid consumer, AgentInstallation provider, CancellationToken ct)
    {
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).Include(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == consumer && x.BusinessId == business.ToString("D") && x.IsEnabled &&
                x.RevisionStatus == PluginRevisionStatus.Active, ct) ?? throw Denied();
        var manifest = Manifest(installation); var providerManifest = Manifest(provider); var profile = Profile(provider);
        var dependency = manifest.Dependencies.SingleOrDefault(x => x.PluginId == providerManifest.Id && x.PublisherId == providerManifest.Publisher.Id) ?? throw Denied();
        var capabilities = manifest.Requires.Where(r => r.Dependency == dependency.Id).Select(r => r.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!Version.TryParse(providerManifest.Version, out var version) || !Version.TryParse(dependency.MinimumVersion, out var min) ||
            !Version.TryParse(dependency.MaximumVersionExclusive, out var max) || version < min || version >= max || installation.Grant is null ||
            capabilities.Length == 0 ||
            capabilities.Any(c => !profile.Declares(c) || !Values(installation.Grant.RequiredCapabilitiesJson).Contains(c) ||
                !Values(provider.Grant!.ProvidedCapabilitiesJson).Contains(c))) throw Denied();
        return (installation, capabilities);
    }
    public async Task<RemoteMcpAccountView> GetAsync(Guid owner, Guid business, Guid provider, CancellationToken ct)
    {
        await RequireOwner(db, owner, business, ct); var installation = await Provider(business, provider, ct, management: true);
        var scope = Scope(owner, business, provider);
        var prefix = $"{owner:N}.";
        var records = await db.PluginOperationalStates.AsNoTracking().Where(x => x.OrganizationId == business && x.AgentInstallationId == provider &&
            x.Kind == "remote-mcp-account.v1" && x.ExternalKey.StartsWith(prefix)).ToArrayAsync(ct);
        var connections = new List<RemoteMcpConnectionView>();
        foreach (var row in records)
        {
            var info = await Vault(scope).InspectAsync(scope, row.Id, ct);
            connections.Add(new(row.Id, info.Reference.Generation, info.Revoked, info.Grants.Select(g => (Guid?)g.ConsumerInstallation).Distinct().SingleOrDefault()));
        }
        var candidates = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).Where(x => x.BusinessId == business.ToString("D") &&
            x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active && x.PackageVersion!.PluginKind == PluginKind.Agent).ToArrayAsync(ct);
        var consumers = new List<RemoteMcpConsumerView>();
        foreach (var candidate in candidates)
            try { await Consumer(business, candidate.Id, installation, ct); consumers.Add(new(candidate.Id, candidate.PackageVersion!.AgentName)); }
            catch (UnauthorizedAccessException) { }
        var profile = Profile(installation);
        return new(installation.PackageVersion!.PackageDigest!, connections, consumers)
        {
            ProviderName = profile.ProviderName,
            EndpointHost = profile.Host,
            Tools = profile.Tools.OrderBy(t => t.Capability, StringComparer.Ordinal)
                .Select(t => new RemoteMcpToolView(t.Capability, t.Description)).ToArray()
        };
    }
    public async Task<RemoteMcpConnectionView> ConnectAsync(Guid owner, Guid business, Guid provider, RemoteMcpKeyRequest request, CancellationToken ct)
    {
        await RequireOwner(db, owner, business, ct); await Provider(business, provider, ct);
        var scope = Scope(owner, business, provider);
        var account = await Vault(scope).ConnectAsync(scope, request.Key, ct);
        return new(account.Id, account.Generation, false, null);
    }
    public async Task RotateAsync(Guid owner, Guid business, Guid provider, Guid connection, RemoteMcpKeyRequest request, CancellationToken ct)
    {
        await RequireOwner(db, owner, business, ct); await Provider(business, provider, ct);
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var scope = Scope(owner, business, provider);
        await Vault(scope).RotateAsync(scope, new(connection, request.ExpectedGeneration ?? throw Denied()), request.Key, ct);
        await Invalidate(business, provider, connection, ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }
    public async Task GrantAsync(Guid owner, Guid business, Guid provider, Guid connection, RemoteMcpGrantRequest request, CancellationToken ct)
    {
        await RequireOwner(db, owner, business, ct); var installation = await Provider(business, provider, ct);
        if (request.PackageDigest != installation.PackageVersion!.PackageDigest) throw Denied();
        var (consumer, capabilities) = await ConsumerWithCapabilities(business, request.ConsumerInstallationId, installation, ct);
        var dependency = Manifest(consumer).Dependencies.Single(x => x.PluginId == Manifest(installation).Id);
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var scope = Scope(owner, business, provider);
        var granted = await Vault(scope).GrantAsync(scope, new(connection, request.ExpectedGeneration), consumer.Id, capabilities, ct);
        await Invalidate(business, provider, connection, ct);
        // One explicit account choice per consumer/dependency. Never let agent arguments choose among accounts.
        var old = await db.AgentCapabilityBindings.Where(x => x.RequesterInstallationId == consumer.Id && x.DependencyId == dependency.Id && x.RevokedAt == null).ToArrayAsync(ct);
        foreach (var binding in old) binding.RevokedAt = DateTimeOffset.UtcNow;
        await FenceConsumers(old.Select(b => b.RequesterInstallationId).Distinct().ToArray(), business, ct);
        foreach (var capability in capabilities)
        {
            var id = Guid.NewGuid();
            db.AgentCapabilityBindings.Add(new() { Id = id, OrganizationId = business.ToString("D"), RequesterInstallationId = consumer.Id,
                ProviderInstallationId = provider, DependencyId = dependency.Id, Capability = capability, ProviderPackageDigest = request.PackageDigest,
                GrantRevision = consumer.Grant!.GrantRevision, Origin = AgentCapabilityBindingOrigins.Explicit, ApprovedAt = DateTimeOffset.UtcNow });
            db.PluginOperationalStates.Add(new() { Id = id, OrganizationId = business, AgentInstallationId = provider, Kind = BindingKind,
                ExternalKey = id.ToString("N"), PayloadJson = JsonSerializer.Serialize(new Selection(owner, connection, granted.Generation), Json),
                Revision = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        }
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }
    public async Task RevokeAsync(Guid owner, Guid business, Guid provider, Guid connection, long generation, CancellationToken ct)
    {
        await RequireOwner(db, owner, business, ct);
        if (!await db.AgentInstallations.AnyAsync(x => x.Id == provider && x.BusinessId == business.ToString("D"), ct)) throw Denied();
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var scope = Scope(owner, business, provider);
        await Vault(scope).RevokeAsync(scope, new(connection, generation), ct);
        await Invalidate(business, provider, connection, ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }
    private async Task Invalidate(Guid business, Guid provider, Guid connection, CancellationToken ct)
    {
        var rows = await db.PluginOperationalStates.Where(x => x.OrganizationId == business && x.AgentInstallationId == provider && x.Kind == BindingKind).ToArrayAsync(ct);
        var ids = rows.Where(x => JsonSerializer.Deserialize<Selection>(x.PayloadJson, Json)?.Connection == connection).Select(x => x.Id).ToArray();
        var bindings = await db.AgentCapabilityBindings.Where(x => ids.Contains(x.Id) && x.RevokedAt == null).ToArrayAsync(ct);
        foreach (var binding in bindings) binding.RevokedAt = DateTimeOffset.UtcNow;
        await FenceConsumers(bindings.Select(b => b.RequesterInstallationId).Distinct().ToArray(), business, ct);
        await db.SaveChangesAsync(ct);
    }
    private async Task FenceConsumers(Guid[] consumers, Guid business, CancellationToken ct)
    {
        foreach (var session in await db.McpAgentSessions.Where(x => consumers.Contains(x.AgentInstallationId) &&
            x.OrganizationId == business.ToString("D") && x.RevokedAt == null).ToArrayAsync(ct))
        { session.RevokedAt = DateTimeOffset.UtcNow; session.RevocationReason = "Native account access changed."; }
        var queued = await db.AgentWorkItems.Where(x => consumers.Contains(x.AgentInstallationId) && x.OrganizationId == business.ToString("D") &&
            (x.Status == AgentWorkStatus.Pending || x.Status == AgentWorkStatus.Leased)).Select(x => x.Id).ToArrayAsync(ct);
        var inbox = new AgentWorkInbox(db, protection, TimeProvider.System);
        foreach (var id in queued) await inbox.CancelAsync(id, "Native account access changed; submit new work after reviewing access.", ct);
    }
    private async Task<(AgentCapabilityBinding Binding, Selection Selection, AgentInstallation Provider)> Resolve(Guid business, Guid consumer, string capability, CancellationToken ct)
    {
        var bindings = await db.AgentCapabilityBindings.AsNoTracking().Where(x => x.OrganizationId == business.ToString("D") &&
            x.RequesterInstallationId == consumer && x.Capability == capability && x.RevokedAt == null).ToArrayAsync(ct);
        if (bindings.Length != 1) throw Denied(); var binding = bindings[0];
        var provider = await Provider(business, binding.ProviderInstallationId, ct);
        var requester = await Consumer(business, consumer, provider, ct);
        if (binding.GrantRevision != requester.Grant!.GrantRevision || binding.ProviderPackageDigest != provider.PackageVersion!.PackageDigest) throw Denied();
        var row = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == binding.Id && x.Kind == BindingKind &&
            x.OrganizationId == business && x.AgentInstallationId == provider.Id, ct) ?? throw Denied();
        var selection = JsonSerializer.Deserialize<Selection>(row.PayloadJson, Json) ?? throw Denied();
        await RequireOwner(db, selection.Owner, business, ct);
        return (binding, selection, provider);
    }
    public async Task<bool> IsAvailableAsync(Guid business, Guid consumer, string capability, CancellationToken ct)
    {
        try
        {
            var (binding, selection, _) = await Resolve(business, consumer, capability, ct);
            var scope = Scope(selection.Owner, business, binding.ProviderInstallationId);
            var info = await Vault(scope).InspectAsync(scope, selection.Connection, ct);
            return !info.Revoked && info.Reference.Generation == selection.Generation && info.Grants.Contains(new(consumer, capability));
        }
        catch (UnauthorizedAccessException) { return false; }
    }
    public async Task<JsonElement> ReadAsync(Guid business, Guid consumer, string capability, JsonElement input, CancellationToken ct,
        Func<AccountVault, ReviewedRemoteMcpProfile, RemoteMcpBroker>? brokerFactory = null)
    {
        var (binding, selection, provider) = await Resolve(business, consumer, capability, ct);
        var profile = Profile(provider);
        profile.ValidateInput(capability, input);
        var scope = Scope(selection.Owner, business, binding.ProviderInstallationId);
        async Task Revalidate(CancellationToken token)
        {
            var current = await Resolve(business, consumer, capability, token);
            if (current.Binding.Id != binding.Id || current.Selection != selection) throw Denied();
        }
        var vault = Vault(scope);
        return await (brokerFactory?.Invoke(vault, profile) ?? new RemoteMcpBroker(vault, profile)).ReadAsync(scope,
            new(selection.Connection, selection.Generation), consumer, capability, input, ct, Revalidate);
    }
}
