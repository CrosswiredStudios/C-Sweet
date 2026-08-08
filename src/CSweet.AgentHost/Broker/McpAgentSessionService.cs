using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed record McpSessionIssue(
    AgentSession Session,
    string AccessToken,
    DateTimeOffset ExpiresAt,
    AgentIdentity? Identity,
    AgentRuntimeConfiguration? Configuration);

public sealed class McpAgentSessionService(
    CSweetDbContext db,
    AgentEmployeeIdentityResolver identityResolver,
    IAgentRuntimeSignalService runtimeSignals,
    IAgentConfigurationService configurations,
    AgentWorkInbox inbox,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RotationOverlap = TimeSpan.FromSeconds(30);

    public async Task<McpSessionIssue> EstablishAsync(
        string workloadToken,
        Guid runtimeInstanceId,
        Guid tickId,
        Guid installationId,
        string organizationId,
        string agentId,
        string agentVersion,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var runtime = await db.AgentRuntimeInstances
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.PackageVersion)
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Grant)
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Configuration)
            .SingleOrDefaultAsync(x => x.Id == runtimeInstanceId, cancellationToken)
            ?? throw new UnauthorizedAccessException("The runtime instance was not found.");
        var installation = runtime.AgentInstallation
            ?? throw new UnauthorizedAccessException("The runtime installation was not found.");
        var package = installation.PackageVersion
            ?? throw new UnauthorizedAccessException("The package revision was not found.");
        var grant = installation.Grant
            ?? throw new UnauthorizedAccessException("The installation has no approved grant.");

        if (runtime.TickId != tickId ||
            runtime.AgentInstallationId != installationId ||
            installation.Id != installationId ||
            !installation.IsEnabled ||
            installation.RevisionStatus != PluginRevisionStatus.Active ||
            installation.BusinessId != organizationId ||
            package.AgentId != agentId ||
            package.Version != agentVersion ||
            package.Status is not (AgentPackageVersionStatus.Approved or AgentPackageVersionStatus.Built) ||
            runtime.RuntimeDeadlineAt <= now ||
            runtime.Status is not (AgentRuntimeStatus.WaitingForMcpSession or AgentRuntimeStatus.Running) ||
            !FixedHashEquals(runtime.BrokerTokenHash, Hash(workloadToken)))
            throw new UnauthorizedAccessException("The MCP initialization identity is invalid or no longer active.");

        await RevokeRuntimeSessionsAsync(runtime.Id, "A new MCP session was established.", now, cancellationToken);
        await runtimeSignals.RecordMcpSessionEstablishedAsync(
            runtime.Id,
            runtime.TickId,
            installation.Id,
            workloadToken,
            cancellationToken);

        var accessToken = Base64Url(RandomNumberGenerator.GetBytes(32));
        var entity = new McpAgentSession
        {
            Id = Guid.NewGuid(),
            RuntimeInstanceId = runtime.Id,
            TickId = runtime.TickId,
            AgentInstallationId = installation.Id,
            OrganizationId = installation.BusinessId,
            PackageVersionId = package.Id,
            PackageDigest = package.PackageDigest ?? package.ManifestDigest,
            GrantRevision = grant.GrantRevision,
            AccessTokenHash = Hash(accessToken),
            EstablishedAt = now,
            LastRenewedAt = now,
            ExpiresAt = now.Add(SessionLifetime)
        };
        var effectiveConfiguration = await configurations.ResolveInstallationAsync(installation.Id, cancellationToken);
        db.McpAgentSessions.Add(entity);
        if (installation.DesiredConfigurationRevision > installation.AppliedConfigurationRevision)
        {
            installation.ConfigurationSyncStatus = AgentConfigurationSyncStatus.Refreshing;
            installation.ConfigurationSyncLastAttemptAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        if (installation.DesiredConfigurationRevision > installation.AppliedConfigurationRevision)
        {
            await inbox.EnqueueAsync(
                installation.BusinessId,
                installation.Id,
                CSweet.Domain.Setup.AgentWorkKind.ConfigurationUpdate,
                "configuration.update",
                JsonSerializer.SerializeToElement(new
                {
                    effectiveConfiguration.InstallationId,
                    effectiveConfiguration.SchemaVersion,
                    EffectiveSettings = effectiveConfiguration.Settings,
                    ChangedKeys = effectiveConfiguration.Settings.Keys.Order(StringComparer.Ordinal).ToArray(),
                    DesiredRevision = effectiveConfiguration.Revision,
                    EffectiveDigest = effectiveConfiguration.Digest
                }, JsonOptions),
                $"configuration:{installation.Id:N}:{effectiveConfiguration.Revision}",
                now.AddMinutes(5),
                sourceType: "configuration-control-plane",
                sourceId: effectiveConfiguration.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken: cancellationToken);
        }
        AgentRuntimeMetrics.Session("established");

        var session = ToRequestSession(entity, installation, package, grant);
        return new McpSessionIssue(
            session,
            accessToken,
            entity.ExpiresAt,
            await identityResolver.ResolveAsync(session, cancellationToken),
            new AgentRuntimeConfiguration(
                effectiveConfiguration.SchemaVersion,
                effectiveConfiguration.Settings,
                installation.Id,
                effectiveConfiguration.Revision,
                effectiveConfiguration.Digest));
    }

    public async Task<AgentSession?> AuthenticateAsync(
        string accessToken,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(sessionId, out var id) || string.IsNullOrWhiteSpace(accessToken))
        {
            AgentRuntimeMetrics.SessionDenied("invalid_or_revoked");
            return null;
        }
        var now = timeProvider.GetUtcNow();
        var tokenHash = Hash(accessToken);
        var entity = await db.McpAgentSessions
            .Include(x => x.RuntimeInstance)
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.PackageVersion)
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Grant)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity?.RuntimeInstance is null ||
            entity.AgentInstallation?.PackageVersion is null ||
            entity.AgentInstallation.Grant is null ||
            entity.RevokedAt is not null ||
            entity.ExpiresAt <= now ||
            entity.RuntimeInstance.RuntimeDeadlineAt <= now ||
            entity.RuntimeInstance.Status != AgentRuntimeStatus.Running ||
            entity.RuntimeInstanceId != entity.RuntimeInstance.Id ||
            entity.RuntimeInstance.TickId != entity.TickId ||
            entity.RuntimeInstance.AgentInstallationId != entity.AgentInstallationId ||
            !entity.AgentInstallation.IsEnabled ||
            entity.AgentInstallation.RevisionStatus != PluginRevisionStatus.Active ||
            entity.OrganizationId != entity.AgentInstallation.BusinessId ||
            entity.PackageVersionId != entity.AgentInstallation.PackageVersionId ||
            entity.PackageDigest != CurrentPackageDigest(entity.AgentInstallation.PackageVersion) ||
            entity.GrantRevision != entity.AgentInstallation.Grant.GrantRevision ||
            !MatchesCurrentOrOverlapToken(entity, tokenHash, now))
            return null;
        return ToRequestSession(
            entity,
            entity.AgentInstallation,
            entity.AgentInstallation.PackageVersion,
            entity.AgentInstallation.Grant);
    }

    public async Task<McpSessionIssue> RenewAsync(
        AgentSession authenticated,
        CancellationToken cancellationToken)
    {
        var id = Guid.Parse(authenticated.SessionId);
        var now = timeProvider.GetUtcNow();
        var entity = await db.McpAgentSessions
            .Include(x => x.RuntimeInstance)
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.PackageVersion)
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Grant)
            .SingleAsync(x => x.Id == id && x.RevokedAt == null, cancellationToken);
        if (entity.ExpiresAt <= now ||
            entity.RuntimeInstance is null ||
            entity.RuntimeInstance.Status != AgentRuntimeStatus.Running ||
            entity.RuntimeInstance.RuntimeDeadlineAt <= now ||
            entity.RuntimeInstance.TickId != entity.TickId ||
            entity.RuntimeInstance.AgentInstallationId != entity.AgentInstallationId ||
            entity.AgentInstallation?.PackageVersion is null ||
            entity.AgentInstallation.Grant is null ||
            !entity.AgentInstallation.IsEnabled ||
            entity.AgentInstallation.RevisionStatus != PluginRevisionStatus.Active ||
            entity.AgentInstallation.BusinessId != entity.OrganizationId ||
            entity.AgentInstallation.PackageVersionId != entity.PackageVersionId ||
            entity.PackageDigest != CurrentPackageDigest(entity.AgentInstallation.PackageVersion) ||
            entity.GrantRevision != entity.AgentInstallation.Grant.GrantRevision)
            throw new UnauthorizedAccessException("The MCP session can no longer be renewed.");

        var accessToken = Base64Url(RandomNumberGenerator.GetBytes(32));
        entity.PreviousAccessTokenHash = entity.AccessTokenHash;
        entity.PreviousTokenValidUntil = now.Add(RotationOverlap);
        entity.AccessTokenHash = Hash(accessToken);
        entity.LastRenewedAt = now;
        entity.ExpiresAt = now.Add(SessionLifetime);
        await db.SaveChangesAsync(cancellationToken);
        AgentRuntimeMetrics.Session("renewed");
        var session = ToRequestSession(
            entity,
            entity.AgentInstallation,
            entity.AgentInstallation.PackageVersion,
            entity.AgentInstallation.Grant);
        return new McpSessionIssue(
            session,
            accessToken,
            entity.ExpiresAt,
            await identityResolver.ResolveAsync(session, cancellationToken),
            null);
    }

    public async Task RevokeRuntimeSessionsAsync(
        Guid runtimeId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sessions = await db.McpAgentSessions
            .Where(x => x.RuntimeInstanceId == runtimeId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.RevokedAt = now;
            session.RevocationReason = reason;
        }
        if (sessions.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            AgentRuntimeMetrics.Session("revoked");
        }
    }

    private static AgentSession ToRequestSession(
        McpAgentSession session,
        AgentInstallation installation,
        AgentPackageVersion package,
        AgentInstallationGrant grant) =>
        new(
            session.Id.ToString("D"),
            package.AgentId,
            session.AgentInstallationId.ToString("D"),
            session.OrganizationId,
            session.RuntimeInstanceId.ToString("D"),
            session.TickId.ToString("D"),
            AuthorizedGrant(installation, package, grant),
            package.Version);

    private static AuthorizedAgentGrant AuthorizedGrant(AgentInstallation installation,
        AgentPackageVersion package, AgentInstallationGrant grant)
    {
        if (installation.SetupState == PluginSetupState.Ready)
            return new AuthorizedAgentGrant(Deserialize(grant.ProvidedCapabilitiesJson),
                Deserialize(grant.EventSubscriptionsJson), Deserialize(grant.RequiredCapabilitiesJson),
                grant.GrantRevision);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(package.ManifestJson, JsonOptions);
        var bootstrap = (manifest?.Provides ?? [])
            .Where(x => string.Equals(x.RiskClass, "bootstrap", StringComparison.Ordinal))
            .Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var required = Deserialize(grant.RequiredCapabilitiesJson)
            .Where(x => x is PluginPlatformCapabilities.WebFetch or PluginPlatformCapabilities.WebRequest)
            .ToHashSet(StringComparer.Ordinal);
        return new AuthorizedAgentGrant(bootstrap, new HashSet<string>(StringComparer.Ordinal), required,
            grant.GrantRevision);
    }

    private static IReadOnlySet<string> Deserialize(string value)
    {
        return (JsonSerializer.Deserialize<IReadOnlyList<string>>(value, JsonOptions) ?? [])
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool MatchesCurrentOrOverlapToken(
        McpAgentSession session,
        string tokenHash,
        DateTimeOffset now) =>
        FixedHashEquals(session.AccessTokenHash, tokenHash) ||
        (session.PreviousTokenValidUntil > now &&
         session.PreviousAccessTokenHash is not null &&
         FixedHashEquals(session.PreviousAccessTokenHash, tokenHash));

    private static bool FixedHashEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string CurrentPackageDigest(AgentPackageVersion package) =>
        package.PackageDigest ?? package.ManifestDigest;
}
