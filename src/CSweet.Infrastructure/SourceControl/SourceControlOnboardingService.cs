using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CSweet.Infrastructure.SourceControl;

public sealed class SourceControlOnboardingService(
    CSweetDbContext db,
    ITrustedSourceControlHostClient sourceHost,
    ITrustedProvisioningHostClient provisionerHost,
    ISourceControlPlatformConfigurationProvider platformSetup,
    IGitHubUserAuthorizationClient githubUsers,
    TimeProvider timeProvider) : ISourceControlOnboardingService
{
    internal SourceControlOnboardingService(
        CSweetDbContext db,
        ITrustedSourceControlHostClient sourceHost,
        ITrustedProvisioningHostClient provisionerHost,
        IConfiguration configuration,
        IGitHubUserAuthorizationClient githubUsers,
        TimeProvider timeProvider)
        : this(db, sourceHost, provisionerHost,
            new LegacyPlatformConfigurationProvider(configuration), githubUsers, timeProvider)
    {
    }
    public async Task<SourceControlDashboardResponse> GetDashboardAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var actor = await RequireActorAsync(organizationId, applicationUserId, false, cancellationToken);
        var connections = await db.SourceControlConnections.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId)
            .OrderBy(candidate => candidate.Name)
            .Select(candidate => new SourceControlConnectionSummary(
                candidate.Id,
                candidate.Name,
                candidate.Provider.ToString(),
                candidate.Mode.ToString(),
                candidate.AccountLogin,
                candidate.AccountType,
                candidate.Status.ToString(),
                candidate.Provider == SourceControlProvider.InternalGit || candidate.SourceAccessInstallationId.HasValue,
                candidate.Provider == SourceControlProvider.InternalGit || candidate.ProvisionerInstallationId.HasValue,
                candidate.Repositories.Count(repository => repository.ArchivedAt == null),
                candidate.LastVerifiedAt,
                candidate.LastHealthError,
                candidate.Revision))
            .ToListAsync(cancellationToken);
        var repositories = await db.SourceControlRepositories.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId && candidate.ArchivedAt == null)
            .OrderBy(candidate => candidate.Name)
            .Select(candidate => new SourceControlRepositorySummary(
                candidate.Id,
                candidate.ConnectionId,
                candidate.Name,
                candidate.CanonicalPath,
                candidate.DefaultBranch,
                candidate.Status.ToString(),
                candidate.IsPrivate,
                candidate.IsManaged,
                candidate.LastVerifiedAt,
                candidate.LastHealthError))
            .ToListAsync(cancellationToken);
        var active = await db.SourceControlOnboardingSessions.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId &&
                                candidate.Status != SourceControlOnboardingStatus.Completed &&
                                candidate.Status != SourceControlOnboardingStatus.Cancelled)
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .Select(candidate => new SourceControlOnboardingSummary(
                candidate.Id,
                candidate.ConnectionId,
                candidate.SelectedMode.ToString(),
                candidate.Status.ToString(),
                candidate.CurrentStep,
                candidate.ExpiresAt))
            .FirstOrDefaultAsync(cancellationToken);
        return new SourceControlDashboardResponse(
            connections,
            repositories,
            active,
            await platformSetup.GetReadinessAsync(cancellationToken),
            actor.PermissionLevel >= OrganizationPermissionLevel.Manager);
    }

    public async Task<StartSourceControlOnboardingResponse> StartAsync(
        Guid organizationId,
        Guid applicationUserId,
        StartSourceControlOnboardingRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await RequireActorAsync(organizationId, applicationUserId, true, cancellationToken);
        if (!Enum.TryParse<SourceControlConnectionMode>(request.Mode, true, out var mode) ||
            mode is not (SourceControlConnectionMode.ManagedGitHub or SourceControlConnectionMode.ExistingGitHub))
            throw new ArgumentException("Choose Managed GitHub or Existing GitHub projects.");
        var readiness = await platformSetup.GetReadinessAsync(cancellationToken);
        if (!readiness.ExistingGitHubAvailable ||
            mode == SourceControlConnectionMode.ManagedGitHub && !readiness.ManagedGitHubAvailable)
            throw new InvalidOperationException(readiness.UserMessage ??
                "The selected GitHub connection mode is not configured for this C-Sweet installation.");
        var installUrl = await platformSetup.GetInstallUrlAsync(
            PlatformGitHubAppKind.SourceAccess, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var session = await db.SourceControlOnboardingSessions.SingleOrDefaultAsync(candidate =>
            candidate.OrganizationId == organizationId &&
            candidate.StartedByOrganizationUserId == actor.Id &&
            candidate.SelectedMode == mode &&
            candidate.Status != SourceControlOnboardingStatus.Completed &&
            candidate.Status != SourceControlOnboardingStatus.Cancelled &&
            candidate.ExpiresAt > now,
            cancellationToken);
        session ??= new SourceControlOnboardingSession
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            StartedByOrganizationUserId = actor.Id,
            SelectedMode = mode,
            Status = SourceControlOnboardingStatus.AwaitingProvider,
            CurrentStep = "authorize-source-access",
            DraftJson = "{}",
            CreatedAt = now
        };
        var state = RotateState(session, now);
        session.CurrentStep = "authorize-source-access";
        session.Status = SourceControlOnboardingStatus.AwaitingProvider;
        if (db.Entry(session).State == EntityState.Detached)
            db.SourceControlOnboardingSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return new StartSourceControlOnboardingResponse(
            session.Id,
            mode.ToString(),
            session.CurrentStep,
            AddState(installUrl, state),
            session.ExpiresAt);
    }

    public async Task<CompleteGitHubAppInstallationResponse> CompleteGitHubInstallationAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid sessionId,
        CompleteGitHubAppInstallationRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = await RequireActorAsync(organizationId, applicationUserId, true, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var session = await db.SourceControlOnboardingSessions.SingleOrDefaultAsync(candidate =>
            candidate.OrganizationId == organizationId && candidate.Id == sessionId,
            cancellationToken) ?? throw new KeyNotFoundException("The source-control setup session was not found.");
        if (session.StartedByOrganizationUserId != actor.Id ||
            session.Status != SourceControlOnboardingStatus.AwaitingProvider ||
            session.ExpiresAt <= now)
            throw new UnauthorizedAccessException("This source-control setup session is no longer valid for the current user.");
        if (request.InstallationId <= 0 || string.IsNullOrWhiteSpace(request.Code) ||
            !VerifyState(session, request.State))
            throw new UnauthorizedAccessException("The provider setup response did not match this source-control session.");

        var isSource = string.Equals(request.AppKind, "SourceAccess", StringComparison.OrdinalIgnoreCase);
        var isProvisioner = string.Equals(request.AppKind, "Provisioner", StringComparison.OrdinalIgnoreCase);
        if ((!isSource && !isProvisioner) ||
            (isSource && session.CurrentStep != "authorize-source-access") ||
            (isProvisioner && session.CurrentStep != "authorize-provisioner"))
            throw new InvalidOperationException("The provider setup response arrived for the wrong onboarding step.");

        var appKind = isSource ? PlatformGitHubAppKind.SourceAccess : PlatformGitHubAppKind.Provisioner;
        var userAuthorization = await platformSetup.GetUserAuthorizationAsync(appKind, cancellationToken);
        var authorized = await githubUsers.VerifyInstallationAsync(
            userAuthorization, request.Code, request.InstallationId, cancellationToken);
        if (authorized.InstallationId != request.InstallationId)
            throw new UnauthorizedAccessException("GitHub authorized a different App installation.");

        var installation = isSource
            ? await sourceHost.DescribeInstallationAsync(request.InstallationId, cancellationToken)
            : await provisionerHost.DescribeInstallationAsync(request.InstallationId, cancellationToken);
        if (installation.Suspended)
            throw new InvalidOperationException(installation.SuspendedReason ?? "The GitHub App installation is suspended.");
        if (session.SelectedMode == SourceControlConnectionMode.ManagedGitHub &&
            !string.Equals(installation.AccountType, "Organization", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Full access supports GitHub organizations only; personal accounts cannot create managed projects.");

        SourceControlConnection connection;
        if (session.ConnectionId.HasValue)
        {
            connection = await db.SourceControlConnections.SingleAsync(candidate =>
                candidate.OrganizationId == organizationId && candidate.Id == session.ConnectionId.Value,
                cancellationToken);
            if (connection.ProviderAccountId != installation.AccountId.ToString())
                throw new InvalidOperationException("Both GitHub Apps must be installed on the same organization.");
        }
        else
        {
            connection = await db.SourceControlConnections.SingleOrDefaultAsync(candidate =>
                candidate.OrganizationId == organizationId &&
                candidate.Provider == SourceControlProvider.GitHub &&
                candidate.ProviderAccountId == installation.AccountId.ToString(),
                cancellationToken) ?? new SourceControlConnection
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    Provider = SourceControlProvider.GitHub,
                    Mode = session.SelectedMode,
                    ProviderAccountId = installation.AccountId.ToString(),
                    CreatedAt = now
                };
            connection.Mode = session.SelectedMode;
            connection.Name = installation.AccountLogin;
            connection.AccountLogin = installation.AccountLogin;
            connection.AccountType = installation.AccountType;
            if (db.Entry(connection).State == EntityState.Detached)
                db.SourceControlConnections.Add(connection);
            session.ConnectionId = connection.Id;
        }

        string? nextUrl = null;
        var setupComplete = false;
        if (isSource)
        {
            connection.SourceAccessInstallationId = installation.InstallationId;
            if (session.SelectedMode == SourceControlConnectionMode.ManagedGitHub)
            {
                session.CurrentStep = "authorize-provisioner";
                var state = RotateState(session, now);
                nextUrl = AddState(await platformSetup.GetInstallUrlAsync(
                    PlatformGitHubAppKind.Provisioner, cancellationToken), state);
                connection.Status = SourceControlConnectionStatus.Pending;
            }
            else
            {
                connection.Status = SourceControlConnectionStatus.Connected;
                connection.LastVerifiedAt = now;
                session.CurrentStep = "choose-projects";
                session.Status = SourceControlOnboardingStatus.InProgress;
                setupComplete = true;
            }
        }
        else
        {
            connection.ProvisionerInstallationId = installation.InstallationId;
            connection.Status = SourceControlConnectionStatus.Connected;
            connection.LastVerifiedAt = now;
            session.CurrentStep = "configure-managed-projects";
            session.Status = SourceControlOnboardingStatus.InProgress;
            setupComplete = true;
        }
        connection.LastHealthError = null;
        connection.UpdatedAt = now;
        connection.Revision++;
        session.UpdatedAt = now;
        session.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return new CompleteGitHubAppInstallationResponse(
            session.Id,
            connection.Id,
            connection.AccountLogin,
            session.CurrentStep,
            nextUrl,
            setupComplete);
    }

    public async Task<IReadOnlyList<AvailableSourceControlRepository>> ListAvailableRepositoriesAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid connectionId,
        bool templates,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireActorAsync(organizationId, applicationUserId, true, cancellationToken);
        var connection = await RequireConnectionAsync(organizationId, connectionId, cancellationToken);
        var installationId = templates
            ? connection.ProvisionerInstallationId
            : connection.SourceAccessInstallationId;
        if (!installationId.HasValue)
            throw new InvalidOperationException(templates
                ? "Private-project creation is not connected."
                : "Code-project access is not connected.");
        var repositories = templates
            ? await provisionerHost.ListRepositoriesAsync(installationId.Value, cancellationToken)
            : await sourceHost.ListRepositoriesAsync(installationId.Value, cancellationToken);
        return repositories
            .Where(repository =>
                !repository.IsArchived &&
                (!templates || repository.IsTemplate) &&
                string.Equals(repository.Owner, connection.AccountLogin, StringComparison.OrdinalIgnoreCase))
            .OrderBy(repository => repository.Name)
            .Take(1000)
            .Select(repository => new AvailableSourceControlRepository(
                repository.RepositoryId.ToString(),
                repository.Name,
                repository.FullName,
                repository.DefaultBranch,
                repository.IsPrivate,
                repository.IsTemplate))
            .ToList();
    }

    public async Task<IReadOnlyList<SourceControlRepositorySummary>> SelectExistingRepositoriesAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid connectionId,
        SelectExistingCodeProjectsRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireActorAsync(organizationId, applicationUserId, true, cancellationToken);
        var connection = await RequireConnectionAsync(organizationId, connectionId, cancellationToken);
        if (connection.Mode != SourceControlConnectionMode.ExistingGitHub ||
            !connection.SourceAccessInstallationId.HasValue)
            throw new InvalidOperationException("This connection does not manage an existing-project selection.");
        var selectedIds = ParseRepositoryIds(request.RepositoryIds, 500);
        if (selectedIds.Count == 0)
            throw new ArgumentException("Choose at least one code project.");
        var available = await sourceHost.ListRepositoriesAsync(
            connection.SourceAccessInstallationId.Value, cancellationToken);
        var selected = ResolveSelectedRepositories(connection, available, selectedIds, requireTemplate: false);
        var selectedProviderKeys = selected
            .Select(repository => GitHubRepositoryKey(repository.RepositoryId))
            .ToArray();
        if (await db.SourceControlRepositories.AsNoTracking().AnyAsync(candidate =>
                candidate.OrganizationId != organizationId &&
                selectedProviderKeys.Contains(candidate.ProviderRepositoryKey),
                cancellationToken))
            throw new UnauthorizedAccessException(
                "One of these GitHub projects is already connected to another C-Sweet business.");
        var now = timeProvider.GetUtcNow();
        foreach (var providerRepository in selected)
        {
            var externalId = providerRepository.RepositoryId.ToString();
            var repository = await db.SourceControlRepositories.SingleOrDefaultAsync(candidate =>
                candidate.OrganizationId == organizationId &&
                candidate.ConnectionId == connectionId &&
                candidate.ExternalRepositoryId == externalId,
                cancellationToken) ?? new SourceControlRepository
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    ConnectionId = connectionId,
                    ExternalRepositoryId = externalId,
                    CreatedAt = now
                };
            ApplyProviderRepository(repository, providerRepository, isManaged: false, now);
            if (db.Entry(repository).State == EntityState.Detached)
                db.SourceControlRepositories.Add(repository);
        }
        CompleteOnboarding(connectionId, now);
        await db.SaveChangesAsync(cancellationToken);
        return await db.SourceControlRepositories.AsNoTracking()
            .Where(candidate => candidate.OrganizationId == organizationId &&
                                candidate.ConnectionId == connectionId &&
                                candidate.ArchivedAt == null)
            .OrderBy(candidate => candidate.Name)
            .Select(candidate => new SourceControlRepositorySummary(
                candidate.Id, candidate.ConnectionId, candidate.Name, candidate.CanonicalPath,
                candidate.DefaultBranch, candidate.Status.ToString(), candidate.IsPrivate,
                candidate.IsManaged, candidate.LastVerifiedAt, candidate.LastHealthError))
            .ToListAsync(cancellationToken);
    }

    public async Task<ManagedCodeProjectPolicyResponse> ConfigureManagedRepositoriesAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid connectionId,
        ConfigureManagedCodeProjectsRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireActorAsync(organizationId, applicationUserId, true, cancellationToken);
        var connection = await RequireConnectionAsync(organizationId, connectionId, cancellationToken);
        if (connection.Mode != SourceControlConnectionMode.ManagedGitHub ||
            !connection.SourceAccessInstallationId.HasValue ||
            !connection.ProvisionerInstallationId.HasValue ||
            !string.Equals(connection.AccountType, "Organization", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Full access requires both GitHub Apps on one organization.");
        var prefix = NormalizePrefix(request.NamePrefix);
        if (request.MaximumProjects is < 1 or > 500)
            throw new ArgumentException("The managed private-project quota must be between 1 and 500.");
        var selectedIds = ParseRepositoryIds(request.TemplateRepositoryIds, 20);
        if (selectedIds.Count == 0)
            throw new ArgumentException("Choose at least one approved starter project.");
        var defaultTeam = await db.OrganizationTeams.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.OrganizationId == organizationId &&
            candidate.Id == request.DefaultTeamId &&
            candidate.ArchivedAt == null,
            cancellationToken) ?? throw new ArgumentException("Choose an active default software team.");
        if (defaultTeam.LeadOrganizationUserId == Guid.Empty)
            throw new ArgumentException("The default software team needs a team lead before it can receive code projects.");

        var available = await provisionerHost.ListRepositoriesAsync(
            connection.ProvisionerInstallationId.Value, cancellationToken);
        var selected = ResolveSelectedRepositories(connection, available, selectedIds, requireTemplate: true);
        var now = timeProvider.GetUtcNow();
        var templateIds = new List<Guid>();
        foreach (var providerRepository in selected)
        {
            var externalId = providerRepository.RepositoryId.ToString();
            var template = await db.SourceControlRepositoryTemplates.SingleOrDefaultAsync(candidate =>
                candidate.OrganizationId == organizationId &&
                candidate.ConnectionId == connectionId &&
                candidate.ExternalRepositoryId == externalId,
                cancellationToken) ?? new SourceControlRepositoryTemplate
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    ConnectionId = connectionId,
                    ExternalRepositoryId = externalId,
                    CreatedAt = now
                };
            template.Owner = providerRepository.Owner;
            template.Name = providerRepository.Name;
            template.DisplayName = providerRepository.Name;
            template.DefaultBranch = providerRepository.DefaultBranch;
            template.IsEnabled = true;
            template.UpdatedAt = now;
            template.Revision++;
            if (db.Entry(template).State == EntityState.Detached)
                db.SourceControlRepositoryTemplates.Add(template);
            templateIds.Add(template.Id);
        }

        var policy = await db.RepositoryProvisioningPolicies.SingleOrDefaultAsync(candidate =>
            candidate.OrganizationId == organizationId && candidate.ConnectionId == connectionId,
            cancellationToken);
        policy ??= new RepositoryProvisioningPolicy
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ConnectionId = connectionId,
            CreatedAt = now
        };
        if (request.ExpectedPolicyRevision.HasValue && policy.Revision != request.ExpectedPolicyRevision.Value)
            throw new DbUpdateConcurrencyException("The managed-project policy changed; refresh before saving again.");
        policy.DefaultTeamId = request.DefaultTeamId;
        policy.NamePrefix = prefix;
        policy.NamingPattern = "{prefix}-{slug}";
        policy.ApprovedTemplatesJson = JsonSerializer.Serialize(templateIds);
        policy.MaximumRepositories = request.MaximumProjects;
        policy.RequiresManagerApproval = request.RequiresManagerApproval;
        policy.IsEnabled = true;
        policy.UpdatedAt = now;
        policy.Revision++;
        if (db.Entry(policy).State == EntityState.Detached)
            db.RepositoryProvisioningPolicies.Add(policy);
        CompleteOnboarding(connectionId, now);
        await db.SaveChangesAsync(cancellationToken);
        return new ManagedCodeProjectPolicyResponse(
            policy.Id, connectionId, templateIds, policy.NamePrefix,
            policy.MaximumRepositories, policy.RequiresManagerApproval,
            request.DefaultTeamId, policy.Revision);
    }

    private async Task<SourceControlConnection> RequireConnectionAsync(
        Guid organizationId,
        Guid connectionId,
        CancellationToken cancellationToken) =>
        await db.SourceControlConnections.SingleOrDefaultAsync(candidate =>
            candidate.OrganizationId == organizationId &&
            candidate.Id == connectionId &&
            candidate.Status == SourceControlConnectionStatus.Connected,
            cancellationToken) ?? throw new KeyNotFoundException(
            "The source-control connection is unavailable or belongs to another business.");

    private void CompleteOnboarding(Guid connectionId, DateTimeOffset now)
    {
        var session = db.SourceControlOnboardingSessions.Local
            .FirstOrDefault(candidate => candidate.ConnectionId == connectionId) ??
            db.SourceControlOnboardingSessions.FirstOrDefault(candidate =>
                candidate.ConnectionId == connectionId &&
                candidate.Status != SourceControlOnboardingStatus.Completed &&
                candidate.Status != SourceControlOnboardingStatus.Cancelled);
        if (session is null)
            return;
        session.Status = SourceControlOnboardingStatus.Completed;
        session.CurrentStep = "ready";
        session.CompletedAt = now;
        session.UpdatedAt = now;
        session.Revision++;
    }

    private static IReadOnlyList<TrustedRepositoryDescriptor> ResolveSelectedRepositories(
        SourceControlConnection connection,
        IReadOnlyList<TrustedRepositoryDescriptor> available,
        IReadOnlySet<long> selectedIds,
        bool requireTemplate)
    {
        var selected = available.Where(repository => selectedIds.Contains(repository.RepositoryId)).ToList();
        if (selected.Count != selectedIds.Count || selected.Any(repository =>
                repository.IsArchived ||
                (requireTemplate && !repository.IsTemplate) ||
                !string.Equals(repository.Owner, connection.AccountLogin, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException(
                "One or more selected code projects are not available to this exact GitHub connection.");
        return selected;
    }

    private static HashSet<long> ParseRepositoryIds(IReadOnlyList<string>? values, int maximum)
    {
        if (values is null || values.Count > maximum)
            throw new ArgumentException($"Choose no more than {maximum} code projects.");
        var result = new HashSet<long>();
        foreach (var value in values)
        {
            if (!long.TryParse(value, out var parsed) || parsed <= 0)
                throw new ArgumentException("A selected code project identifier is invalid.");
            result.Add(parsed);
        }
        return result;
    }

    private static string NormalizePrefix(string value)
    {
        var prefix = value.Trim().ToLowerInvariant();
        if (prefix.Length is < 1 or > 50 ||
            prefix[0] == '-' || prefix[^1] == '-' ||
            prefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("Use a 1-50 character project prefix with lowercase letters, numbers, and single hyphens.");
        return prefix;
    }

    private static void ApplyProviderRepository(
        SourceControlRepository repository,
        TrustedRepositoryDescriptor provider,
        bool isManaged,
        DateTimeOffset now)
    {
        repository.ProviderRepositoryKey = GitHubRepositoryKey(provider.RepositoryId);
        repository.Owner = provider.Owner;
        repository.Name = provider.Name;
        repository.CanonicalPath = provider.FullName.ToLowerInvariant();
        repository.CloneUrl = provider.CloneUrl;
        repository.DefaultBranch = provider.DefaultBranch;
        repository.IsPrivate = provider.IsPrivate;
        repository.IsManaged = isManaged;
        repository.Status = SourceControlRepositoryStatus.Ready;
        repository.LastVerifiedAt = now;
        repository.LastHealthError = null;
        repository.ArchivedAt = null;
        repository.UpdatedAt = now;
        repository.Revision++;
    }

    private static string GitHubRepositoryKey(long repositoryId) => $"github:{repositoryId}";

    private async Task<OrganizationUser> RequireActorAsync(
        Guid organizationId,
        Guid applicationUserId,
        bool requireManager,
        CancellationToken cancellationToken)
    {
        var actor = await db.CoreOrganizationUsers.SingleOrDefaultAsync(candidate =>
            candidate.OrganizationId == organizationId &&
            candidate.ApplicationUserId == applicationUserId &&
            candidate.IsActive,
            cancellationToken) ?? throw new UnauthorizedAccessException(
            "The current user is not an active member of this business.");
        if (requireManager && actor.PermissionLevel < OrganizationPermissionLevel.Manager)
            throw new UnauthorizedAccessException("Only business owners and managers can set up source control.");
        return actor;
    }

    private static string RotateState(SourceControlOnboardingSession session, DateTimeOffset now)
    {
        var state = $"{session.OrganizationId:N}.{session.Id:N}.{Base64Url(RandomNumberGenerator.GetBytes(32))}";
        session.StateNonceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
        session.ExpiresAt = now.AddMinutes(20);
        session.UpdatedAt = now;
        session.Revision++;
        return state;
    }

    private static bool VerifyState(SourceControlOnboardingSession session, string supplied)
    {
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(session.StateNonceHash);
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(expected, suppliedHash);
    }

    private static string AddState(string url, string state) =>
        $"{url}{(url.Contains('?', StringComparison.Ordinal) ? '&' : '?')}state={Uri.EscapeDataString(state)}";

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed class LegacyPlatformConfigurationProvider(IConfiguration configuration)
    : ISourceControlPlatformConfigurationProvider
{
    public Task<SourceControlPlatformReadiness> GetReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        var sourceReady = IsTrustedUrl(configuration["CSweet:SourceControl:GitHostBaseUrl"]) &&
                          IsHttpsUrl(configuration["CSweet:SourceControl:SourceAccessInstallUrl"]) &&
                          HasUserAuthorization(PlatformGitHubAppKind.SourceAccess);
        var provisionerReady = sourceReady &&
                               IsTrustedUrl(configuration["CSweet:SourceControl:ProvisionerHostBaseUrl"]) &&
                               IsHttpsUrl(configuration["CSweet:SourceControl:ProvisionerInstallUrl"]) &&
                               HasUserAuthorization(PlatformGitHubAppKind.Provisioner);
        return Task.FromResult(new SourceControlPlatformReadiness(
            sourceReady,
            provisionerReady,
            !sourceReady
                ? "A platform administrator must configure enterprise GitHub source control first."
                : !provisionerReady
                    ? "Existing GitHub projects are ready; managed private projects are not configured."
                    : null,
            sourceReady ? "ExternallyManaged" : "Unconfigured"));
    }

    public Task<string> GetInstallUrlAsync(
        PlatformGitHubAppKind kind,
        CancellationToken cancellationToken = default)
    {
        var value = configuration[$"CSweet:SourceControl:{(kind == PlatformGitHubAppKind.SourceAccess ? "SourceAccess" : "Provisioner")}InstallUrl"];
        if (!IsHttpsUrl(value)) throw new InvalidOperationException(
            "The GitHub App installation flow is not ready.");
        return Task.FromResult(value!);
    }

    public Task<PlatformGitHubUserAuthorizationConfiguration> GetUserAuthorizationAsync(
        PlatformGitHubAppKind kind,
        CancellationToken cancellationToken = default)
    {
        var prefix = kind == PlatformGitHubAppKind.SourceAccess ? "SourceAccess" : "Provisioner";
        var clientId = configuration[$"CSweet:SourceControl:{prefix}ClientId"];
        var clientSecret = configuration[$"CSweet:SourceControl:{prefix}ClientSecret"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("The GitHub sign-in flow is not ready.");
        return Task.FromResult(new PlatformGitHubUserAuthorizationConfiguration(clientId, clientSecret));
    }

    private bool HasUserAuthorization(PlatformGitHubAppKind kind)
    {
        var prefix = kind == PlatformGitHubAppKind.SourceAccess ? "SourceAccess" : "Provisioner";
        return !string.IsNullOrWhiteSpace(configuration[$"CSweet:SourceControl:{prefix}ClientId"]) &&
               !string.IsNullOrWhiteSpace(configuration[$"CSweet:SourceControl:{prefix}ClientSecret"]);
    }

    private static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    private static bool IsTrustedUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" or "https+http";
}
