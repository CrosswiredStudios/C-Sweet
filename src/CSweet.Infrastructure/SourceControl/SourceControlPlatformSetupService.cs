using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CSweet.Infrastructure.SourceControl;

public sealed class SourceControlPlatformSetupService(
    CSweetDbContext db,
    IDataProtectionProvider dataProtectionProvider,
    IPlatformGitHubManifestClient manifests,
    ITrustedSourceControlHostClient sourceHost,
    ITrustedProvisioningHostClient provisionerHost,
    IConfiguration configuration,
    IAuditEventWriter audit,
    TimeProvider timeProvider) : ISourceControlPlatformSetupService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(
        "CSweet.PlatformGitHubAppCredentials.v1");

    public async Task<SourceControlPlatformReadiness> GetReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        var active = await db.PlatformGitHubAppCredentials.AsNoTracking()
            .Where(x => x.Status == PlatformGitHubAppCredentialStatus.Active)
            .ToListAsync(cancellationToken);
        var source = active.SingleOrDefault(x => x.Kind == PlatformGitHubAppKind.SourceAccess);
        var provisioner = active.SingleOrDefault(x => x.Kind == PlatformGitHubAppKind.Provisioner);
        var sourceStatus = await SafeStatusAsync(sourceHost.GetConfigurationStatusAsync, cancellationToken);
        var provisionerStatus = await SafeStatusAsync(provisionerHost.GetConfigurationStatusAsync, cancellationToken);
        var sourceManaged = Matches(source, sourceStatus);
        var provisionerManaged = Matches(provisioner, provisionerStatus);
        var sourceExternal = source is null && sourceStatus.Configured && IsHttpsUrl(
            configuration["CSweet:SourceControl:SourceAccessInstallUrl"]);
        var provisionerExternal = provisioner is null && provisionerStatus.Configured && IsHttpsUrl(
            configuration["CSweet:SourceControl:ProvisionerInstallUrl"]);
        var sourceReady = sourceManaged || sourceExternal;
        var managedReady = sourceReady && (provisionerManaged || provisionerExternal);
        var mode = sourceManaged ? "CSweetManaged" : sourceExternal ? "ExternallyManaged" : "Unconfigured";
        return new SourceControlPlatformReadiness(
            sourceReady,
            managedReady,
            !sourceReady
                ? "Complete the enterprise GitHub setup before businesses connect projects."
                : !managedReady
                    ? "Existing GitHub projects are ready. The optional Repository Provisioner is not enabled."
                    : null,
            mode);
    }

    public async Task<string> GetInstallUrlAsync(
        PlatformGitHubAppKind kind,
        CancellationToken cancellationToken = default)
    {
        var value = await db.PlatformGitHubAppCredentials.AsNoTracking()
            .Where(x => x.Kind == kind && x.Status == PlatformGitHubAppCredentialStatus.Active)
            .Select(x => x.InstallUrl)
            .SingleOrDefaultAsync(cancellationToken);
        value ??= configuration[$"CSweet:SourceControl:{(kind == PlatformGitHubAppKind.SourceAccess ? "SourceAccess" : "Provisioner")}InstallUrl"];
        if (!IsHttpsUrl(value))
            throw new InvalidOperationException("The GitHub App installation flow is not ready.");
        return value!;
    }

    public async Task<PlatformSourceControlSetupResponse> GetAsync(
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var session = await db.PlatformSourceControlSetupSessions.AsNoTracking()
            .Where(x => x.StartedByApplicationUserId == applicationUserId &&
                        x.Status != PlatformSourceControlSetupStatus.Cancelled &&
                        x.Status != PlatformSourceControlSetupStatus.Expired)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return await ProjectAsync(session, cancellationToken);
    }

    public async Task<PlatformSourceControlSetupResponse> StartAsync(
        Guid applicationUserId,
        StartPlatformSourceControlSetupRequest request,
        CancellationToken cancellationToken = default)
    {
        var publicBaseUrl = NormalizePublicBaseUrl(request.PublicBaseUrl);
        var manifestCallbackUrl = NormalizePublicBaseUrl(
            request.ManifestCallbackUrl ?? request.PublicBaseUrl);
        var now = timeProvider.GetUtcNow();
        var existing = await db.PlatformSourceControlSetupSessions
            .Where(x =>
            x.Status != PlatformSourceControlSetupStatus.Active &&
            x.Status != PlatformSourceControlSetupStatus.Cancelled &&
            x.Status != PlatformSourceControlSetupStatus.Expired &&
            x.ExpiresAt > now)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            if (existing.StartedByApplicationUserId != applicationUserId)
                throw new InvalidOperationException(
                    "Another system administrator is already completing enterprise source-control setup.");
            return await ProjectAsync(existing, cancellationToken);
        }
        var activeSource = await db.PlatformGitHubAppCredentials.AsNoTracking()
            .Where(x => x.Kind == PlatformGitHubAppKind.SourceAccess &&
                        x.Status == PlatformGitHubAppCredentialStatus.Active)
            .SingleOrDefaultAsync(cancellationToken);
        var session = new PlatformSourceControlSetupSession
        {
            Id = Guid.NewGuid(),
            StartedByApplicationUserId = applicationUserId,
            PublicBaseUrl = publicBaseUrl,
            ManifestCallbackUrl = manifestCallbackUrl,
            Status = PlatformSourceControlSetupStatus.InProgress,
            CurrentStep = activeSource is not null ? "capabilities" : "organization",
            GitHubOrganization = activeSource?.OwnerLogin ?? string.Empty,
            SourceAccessCredentialId = activeSource?.Id,
            PrerequisitesConfirmed = activeSource is not null,
            SourceAccessPermissionsConfirmed = activeSource is not null,
            SourceAccessAppConfirmed = activeSource is not null,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddHours(24)
        };
        db.PlatformSourceControlSetupSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(session, applicationUserId, "source-control.platform-setup.started", "Started", cancellationToken);
        return await ProjectAsync(session, cancellationToken);
    }

    public async Task<PlatformSourceControlSetupResponse> ConfirmOrganizationAsync(
        Guid applicationUserId,
        Guid sessionId,
        ConfirmPlatformOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(applicationUserId, sessionId, cancellationToken);
        RequireRevision(session, request.ExpectedRevision);
        if (!request.PrerequisitesConfirmed)
            throw new ArgumentException("Confirm that you can create GitHub Apps for this organization.");
        var organization = NormalizeOrganization(request.OrganizationLogin);
        session.GitHubOrganization = organization;
        session.PrerequisitesConfirmed = true;
        session.CurrentStep = "source-access-review";
        Touch(session);
        await db.SaveChangesAsync(cancellationToken);
        return await ProjectAsync(session, cancellationToken);
    }

    public async Task<PlatformSourceControlSetupResponse> ConfirmReviewAsync(
        Guid applicationUserId,
        Guid sessionId,
        PlatformGitHubAppKind kind,
        ConfirmPlatformAppReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(applicationUserId, sessionId, cancellationToken);
        RequireRevision(session, request.ExpectedRevision);
        if (!request.Confirmed)
            throw new ArgumentException("Confirm the requested GitHub permissions before continuing.");
        if (kind == PlatformGitHubAppKind.SourceAccess)
        {
            if (session.CurrentStep != "source-access-review") throw InvalidStep();
            session.SourceAccessPermissionsConfirmed = true;
        }
        else
        {
            if (session.CurrentStep != "provisioner-review" || session.ProvisionerRequested != true)
                throw InvalidStep();
            session.ProvisionerPermissionsConfirmed = true;
        }
        Touch(session);
        await db.SaveChangesAsync(cancellationToken);
        return await ProjectAsync(session, cancellationToken);
    }

    public async Task<PlatformGitHubManifestLaunchResponse> CreateManifestAsync(
        Guid applicationUserId,
        Guid sessionId,
        PlatformGitHubAppKind kind,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(applicationUserId, sessionId, cancellationToken);
        var confirmed = kind == PlatformGitHubAppKind.SourceAccess
            ? session.SourceAccessPermissionsConfirmed && session.CurrentStep == "source-access-review"
            : session.ProvisionerPermissionsConfirmed && session.CurrentStep == "provisioner-review";
        if (!confirmed) throw InvalidStep();
        var now = timeProvider.GetUtcNow();
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        session.StateNonceHash = Hash(state);
        session.StateExpiresAt = now.AddMinutes(20);
        session.PendingAppKind = kind;
        session.Status = PlatformSourceControlSetupStatus.AwaitingGitHub;
        session.LastError = null;
        Touch(session);
        await db.SaveChangesAsync(cancellationToken);
        var manifest = BuildManifest(session, kind);
        var postUrl = $"https://github.com/organizations/{Uri.EscapeDataString(session.GitHubOrganization)}/settings/apps/new?state={Uri.EscapeDataString(state)}";
        return new PlatformGitHubManifestLaunchResponse(
            postUrl, JsonSerializer.Serialize(manifest, JsonOptions), session.StateExpiresAt.Value);
    }

    public async Task<PlatformGitHubManifestCompletion> CompleteManifestAsync(
        Guid applicationUserId,
        string code,
        string state,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state) || state.Length > 256)
            throw new ArgumentException("GitHub returned an invalid setup state.");
        var now = timeProvider.GetUtcNow();
        var stateHash = Hash(state);
        var session = await db.PlatformSourceControlSetupSessions.SingleOrDefaultAsync(x =>
            x.StateNonceHash == stateHash && x.StateExpiresAt > now,
            cancellationToken) ?? throw new InvalidOperationException(
            "This GitHub setup response has expired or was already used.");
        if (session.StartedByApplicationUserId != applicationUserId || session.PendingAppKind is null ||
            session.Status != PlatformSourceControlSetupStatus.AwaitingGitHub)
            throw new UnauthorizedAccessException("This GitHub setup response belongs to a different administrator or session.");
        var kind = session.PendingAppKind.Value;
        // Consume the state before the one-time GitHub code is exchanged.
        session.StateNonceHash = string.Empty;
        session.StateExpiresAt = null;
        session.PendingAppKind = null;
        session.Status = PlatformSourceControlSetupStatus.InProgress;
        Touch(session);
        await db.SaveChangesAsync(cancellationToken);

        PlatformGitHubAppCredential? credential = null;
        try
        {
            var conversion = await manifests.ConvertAsync(code, cancellationToken);
            var privateKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(conversion.PrivateKeyPem));
            credential = new PlatformGitHubAppCredential
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                OwnerLogin = session.GitHubOrganization,
                AppId = conversion.AppId,
                AppName = conversion.AppName,
                AppSlug = conversion.AppSlug,
                InstallUrl = $"https://github.com/apps/{Uri.EscapeDataString(conversion.AppSlug)}/installations/new",
                ProtectedPrivateKey = _protector.Protect(privateKeyBase64),
                Status = PlatformGitHubAppCredentialStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = session.ExpiresAt
            };
            db.PlatformGitHubAppCredentials.Add(credential);
            if (kind == PlatformGitHubAppKind.SourceAccess)
                session.SourceAccessCredentialId = credential.Id;
            else
                session.ProvisionerCredentialId = credential.Id;
            await db.SaveChangesAsync(cancellationToken);

            var validated = await ValidateAsync(credential, privateKeyBase64, cancellationToken);
            if (validated.AppId != credential.AppId ||
                !string.Equals(validated.AppSlug, credential.AppSlug, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The trusted host verified a different GitHub App identity.");
            credential.Status = PlatformGitHubAppCredentialStatus.Verified;
            credential.VerifiedAt = now;
            credential.UpdatedAt = now;
            credential.Revision++;
            session.CurrentStep = kind == PlatformGitHubAppKind.SourceAccess
                ? "source-access-confirm"
                : "provisioner-confirm";
            session.LastError = null;
            Touch(session);
            await db.SaveChangesAsync(cancellationToken);
            await WriteAuditAsync(session, applicationUserId,
                "source-control.platform-app.verified", kind.ToString(), cancellationToken, credential);
            return new PlatformGitHubManifestCompletion(session.Id, session.PublicBaseUrl);
        }
        catch (Exception exception)
        {
            if (credential is not null)
            {
                credential.Status = PlatformGitHubAppCredentialStatus.Failed;
                credential.FailureMessage = SafeFailure(exception.Message);
                credential.UpdatedAt = now;
                credential.Revision++;
            }
            session.LastError = credential is null
                ? "C-Sweet could not finish the GitHub handoff. Prepare a new GitHub setup link to retry."
                : "C-Sweet could not verify the GitHub App. You can retry without copying any secrets.";
            session.CurrentStep = credential is null
                ? kind == PlatformGitHubAppKind.SourceAccess
                    ? "source-access-review"
                    : "provisioner-review"
                : kind == PlatformGitHubAppKind.SourceAccess
                    ? "source-access-confirm"
                    : "provisioner-confirm";
            Touch(session);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<PlatformSourceControlSetupResponse> ConfirmAppAsync(
        Guid applicationUserId,
        Guid sessionId,
        PlatformGitHubAppKind kind,
        ConfirmPlatformAppRequest request,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(applicationUserId, sessionId, cancellationToken);
        RequireRevision(session, request.ExpectedRevision);
        if (!request.Confirmed) throw new ArgumentException("Confirm the verified GitHub App to continue.");
        var credential = await RequireCredentialAsync(session, kind, cancellationToken);
        if (credential.Status == PlatformGitHubAppCredentialStatus.Failed)
        {
            var status = await ValidateAsync(credential, Unprotect(credential), cancellationToken);
            credential.AppName = status.AppName ?? credential.AppName;
            credential.AppSlug = status.AppSlug ?? credential.AppSlug;
            credential.Status = PlatformGitHubAppCredentialStatus.Verified;
            credential.FailureMessage = null;
            credential.VerifiedAt = timeProvider.GetUtcNow();
            credential.UpdatedAt = timeProvider.GetUtcNow();
            credential.Revision++;
        }
        if (credential.Status != PlatformGitHubAppCredentialStatus.Verified)
            throw new InvalidOperationException("The GitHub App must be verified before it can be confirmed.");
        if (kind == PlatformGitHubAppKind.SourceAccess)
        {
            if (session.CurrentStep != "source-access-confirm") throw InvalidStep();
            session.SourceAccessAppConfirmed = true;
            session.CurrentStep = "capabilities";
        }
        else
        {
            if (session.CurrentStep != "provisioner-confirm") throw InvalidStep();
            session.ProvisionerAppConfirmed = true;
            session.CurrentStep = "review-activate";
            session.Status = PlatformSourceControlSetupStatus.ReadyToActivate;
        }
        Touch(session);
        await db.SaveChangesAsync(cancellationToken);
        return await ProjectAsync(session, cancellationToken);
    }

    public async Task<PlatformSourceControlSetupResponse> ChooseProvisionerAsync(
        Guid applicationUserId,
        Guid sessionId,
        ChoosePlatformProvisionerRequest request,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(applicationUserId, sessionId, cancellationToken);
        RequireRevision(session, request.ExpectedRevision);
        if (session.CurrentStep != "capabilities" || !session.SourceAccessAppConfirmed)
            throw InvalidStep();
        session.ProvisionerRequested = request.EnableProvisioner;
        session.CurrentStep = request.EnableProvisioner ? "provisioner-review" : "review-activate";
        session.Status = request.EnableProvisioner
            ? PlatformSourceControlSetupStatus.InProgress
            : PlatformSourceControlSetupStatus.ReadyToActivate;
        Touch(session);
        await db.SaveChangesAsync(cancellationToken);
        return await ProjectAsync(session, cancellationToken);
    }

    public async Task<PlatformSourceControlSetupResponse> ActivateAsync(
        Guid applicationUserId,
        Guid sessionId,
        ActivatePlatformSourceControlRequest request,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(applicationUserId, sessionId, cancellationToken);
        RequireRevision(session, request.ExpectedRevision);
        if (!request.Confirmed || session.CurrentStep != "review-activate" ||
            !session.SourceAccessAppConfirmed ||
            (session.ProvisionerRequested == true && !session.ProvisionerAppConfirmed))
            throw InvalidStep();
        var source = await RequireCredentialAsync(session, PlatformGitHubAppKind.SourceAccess, cancellationToken);
        var provisioner = session.ProvisionerRequested == true
            ? await RequireCredentialAsync(session, PlatformGitHubAppKind.Provisioner, cancellationToken)
            : null;
        var sourceWasActive = source.Status == PlatformGitHubAppCredentialStatus.Active;
        var provisionerWasActive = provisioner?.Status == PlatformGitHubAppCredentialStatus.Active;
        if (!sourceWasActive) source.Status = PlatformGitHubAppCredentialStatus.Activating;
        if (provisioner is not null && !provisionerWasActive)
            provisioner.Status = PlatformGitHubAppCredentialStatus.Activating;
        session.ActivationConfirmed = true;
        Touch(session);
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            await sourceHost.ActivateConfigurationAsync(ToConfiguration(source), cancellationToken);
            if (provisioner is not null)
                await provisionerHost.ActivateConfigurationAsync(ToConfiguration(provisioner), cancellationToken);
            var now = timeProvider.GetUtcNow();
            var newIds = new[] { source.Id, provisioner?.Id }.Where(x => x.HasValue).Select(x => x!.Value).ToHashSet();
            var old = await db.PlatformGitHubAppCredentials.Where(x =>
                x.Status == PlatformGitHubAppCredentialStatus.Active && !newIds.Contains(x.Id))
                .ToListAsync(cancellationToken);
            foreach (var item in old)
            {
                item.Status = PlatformGitHubAppCredentialStatus.Superseded;
                item.SupersededAt = now;
                item.UpdatedAt = now;
                item.Revision++;
            }
            ActivateCredential(source, now);
            if (provisioner is not null) ActivateCredential(provisioner, now);
            session.Status = PlatformSourceControlSetupStatus.Active;
            session.CurrentStep = "complete";
            session.CompletedAt = now;
            session.LastError = null;
            Touch(session);
            await db.SaveChangesAsync(cancellationToken);
            await WriteAuditAsync(session, applicationUserId,
                "source-control.platform-setup.activated", "Activated", cancellationToken);
            return await ProjectAsync(session, cancellationToken);
        }
        catch (Exception exception)
        {
            source.Status = sourceWasActive
                ? PlatformGitHubAppCredentialStatus.Active
                : PlatformGitHubAppCredentialStatus.Verified;
            if (provisioner is not null)
                provisioner.Status = provisionerWasActive
                    ? PlatformGitHubAppCredentialStatus.Active
                    : PlatformGitHubAppCredentialStatus.Verified;
            session.Status = PlatformSourceControlSetupStatus.ReadyToActivate;
            session.LastError = "C-Sweet could not activate the verified configuration. Select Activate again to retry.";
            Touch(session);
            await db.SaveChangesAsync(CancellationToken.None);
            throw new InvalidOperationException(session.LastError, exception);
        }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var active = await db.PlatformGitHubAppCredentials
            .Where(x => x.Status == PlatformGitHubAppCredentialStatus.Active)
            .ToListAsync(cancellationToken);
        foreach (var credential in active)
        {
            var status = credential.Kind == PlatformGitHubAppKind.SourceAccess
                ? await sourceHost.GetConfigurationStatusAsync(cancellationToken)
                : await provisionerHost.GetConfigurationStatusAsync(cancellationToken);
            if (!Matches(credential, status))
            {
                if (credential.Kind == PlatformGitHubAppKind.SourceAccess)
                    await sourceHost.ActivateConfigurationAsync(ToConfiguration(credential), cancellationToken);
                else
                    await provisionerHost.ActivateConfigurationAsync(ToConfiguration(credential), cancellationToken);
            }
        }
        var expiredSessions = await db.PlatformSourceControlSetupSessions.Where(x =>
            x.ExpiresAt <= now && x.Status != PlatformSourceControlSetupStatus.Active &&
            x.Status != PlatformSourceControlSetupStatus.Cancelled &&
            x.Status != PlatformSourceControlSetupStatus.Expired).ToListAsync(cancellationToken);
        foreach (var session in expiredSessions)
        {
            session.Status = PlatformSourceControlSetupStatus.Expired;
            session.UpdatedAt = now;
            session.Revision++;
        }
        var expiredCredentials = await db.PlatformGitHubAppCredentials.Where(x =>
            x.ExpiresAt <= now && x.Status != PlatformGitHubAppCredentialStatus.Active &&
            x.Status != PlatformGitHubAppCredentialStatus.Superseded).ToListAsync(cancellationToken);
        db.PlatformGitHubAppCredentials.RemoveRange(expiredCredentials);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PlatformSourceControlSetupResponse> CancelAsync(
        Guid applicationUserId,
        Guid sessionId,
        CancelPlatformSourceControlSetupRequest request,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireSessionAsync(applicationUserId, sessionId, cancellationToken);
        RequireRevision(session, request.ExpectedRevision);
        if (!request.Confirmed)
            throw new ArgumentException("Confirm that you want to cancel this setup session.");
        if (session.Status == PlatformSourceControlSetupStatus.Active)
            throw new InvalidOperationException("An active enterprise configuration cannot be cancelled.");
        var ids = new[] { session.SourceAccessCredentialId, session.ProvisionerCredentialId }
            .Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var pending = await db.PlatformGitHubAppCredentials.Where(x =>
            ids.Contains(x.Id) && x.Status != PlatformGitHubAppCredentialStatus.Active)
            .ToListAsync(cancellationToken);
        db.PlatformGitHubAppCredentials.RemoveRange(pending);
        session.SourceAccessCredentialId = null;
        session.ProvisionerCredentialId = null;
        session.Status = PlatformSourceControlSetupStatus.Cancelled;
        session.CurrentStep = "cancelled";
        session.CompletedAt = timeProvider.GetUtcNow();
        Touch(session);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(session, applicationUserId,
            "source-control.platform-setup.cancelled", "Cancelled", cancellationToken);
        return await ProjectAsync(session, cancellationToken);
    }

    private async Task<PlatformSourceControlSetupResponse> ProjectAsync(
        PlatformSourceControlSetupSession? session,
        CancellationToken cancellationToken)
    {
        var readiness = await GetReadinessAsync(cancellationToken);
        if (session is null) return new PlatformSourceControlSetupResponse(readiness, null);
        var ids = new[] { session.SourceAccessCredentialId, session.ProvisionerCredentialId }
            .Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var credentials = await db.PlatformGitHubAppCredentials.AsNoTracking()
            .Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
        return new PlatformSourceControlSetupResponse(readiness, new PlatformSourceControlSetupSessionResponse(
            session.Id, session.Status.ToString(), session.CurrentStep, session.GitHubOrganization,
            session.PublicBaseUrl, session.PrerequisitesConfirmed,
            session.SourceAccessPermissionsConfirmed, session.SourceAccessAppConfirmed,
            session.ProvisionerRequested, session.ProvisionerPermissionsConfirmed,
            session.ProvisionerAppConfirmed, session.ActivationConfirmed,
            Summary(credentials.SingleOrDefault(x => x.Kind == PlatformGitHubAppKind.SourceAccess)),
            Summary(credentials.SingleOrDefault(x => x.Kind == PlatformGitHubAppKind.Provisioner)),
            session.LastError, session.ExpiresAt, session.Revision));
    }

    private async Task<PlatformSourceControlSetupSession> RequireSessionAsync(
        Guid applicationUserId, Guid sessionId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var session = await db.PlatformSourceControlSetupSessions.SingleOrDefaultAsync(x =>
            x.Id == sessionId && x.StartedByApplicationUserId == applicationUserId,
            cancellationToken) ?? throw new KeyNotFoundException("The enterprise source-control setup session was not found.");
        if (session.ExpiresAt <= now)
            throw new InvalidOperationException("The enterprise source-control setup session has expired.");
        return session;
    }

    private async Task<PlatformGitHubAppCredential> RequireCredentialAsync(
        PlatformSourceControlSetupSession session,
        PlatformGitHubAppKind kind,
        CancellationToken cancellationToken)
    {
        var id = kind == PlatformGitHubAppKind.SourceAccess
            ? session.SourceAccessCredentialId : session.ProvisionerCredentialId;
        return id.HasValue
            ? await db.PlatformGitHubAppCredentials.SingleOrDefaultAsync(x => x.Id == id.Value, cancellationToken)
                ?? throw new InvalidOperationException("The verified GitHub App credential was not found.")
            : throw new InvalidOperationException("Create and verify the GitHub App before continuing.");
    }

    private Task<TrustedGitHubAppConfigurationStatus> ValidateAsync(
        PlatformGitHubAppCredential credential,
        string privateKeyBase64,
        CancellationToken cancellationToken)
    {
        var request = new TrustedGitHubAppConfiguration(
            credential.AppId, privateKeyBase64, credential.Revision);
        return credential.Kind == PlatformGitHubAppKind.SourceAccess
            ? sourceHost.ValidateConfigurationAsync(request, cancellationToken)
            : provisionerHost.ValidateConfigurationAsync(request, cancellationToken);
    }

    private TrustedGitHubAppConfiguration ToConfiguration(PlatformGitHubAppCredential credential) =>
        new(credential.AppId, Unprotect(credential), credential.Revision);

    private string Unprotect(PlatformGitHubAppCredential credential) =>
        _protector.Unprotect(credential.ProtectedPrivateKey);

    private object BuildManifest(
        PlatformSourceControlSetupSession session,
        PlatformGitHubAppKind kind)
    {
        var isSource = kind == PlatformGitHubAppKind.SourceAccess;
        return new
        {
            name = BuildGitHubAppName(session, kind),
            description = isSource
                ? "Connects approved GitHub repositories to this C-Sweet installation."
                : "Creates governed private repositories for this C-Sweet installation.",
            url = session.PublicBaseUrl,
            redirect_url = $"{session.ManifestCallbackUrl}/api/source-control/platform-setup/github-manifest-callback",
            setup_url = $"{session.PublicBaseUrl}/source-control/github-callback",
            setup_on_update = false,
            request_oauth_on_install = false,
            @public = true,
            default_events = Array.Empty<string>(),
            default_permissions = isSource
                ? (object)new { contents = "write", pull_requests = "write", checks = "read", metadata = "read" }
                : new { administration = "write" }
        };
    }

    private static string BuildGitHubAppName(
        PlatformSourceControlSetupSession session,
        PlatformGitHubAppKind kind)
    {
        const int maximumLength = 34;
        var prefix = kind == PlatformGitHubAppKind.SourceAccess
            ? "C-Sweet Source"
            : "C-Sweet Repo";
        var suffix = session.Id.ToString("N")[..8];
        var rawHost = new Uri(session.PublicBaseUrl).IdnHost.ToLowerInvariant();
        var host = new string(rawHost.Select(character =>
                char.IsAsciiLetterOrDigit(character) || character == '-' ? character : '-')
            .ToArray()).Trim('-');
        if (string.IsNullOrEmpty(host)) host = "app";

        // Prefix + space + host + hyphen + unique suffix must fit GitHub's limit.
        var maximumHostLength = maximumLength - prefix.Length - suffix.Length - 2;
        if (host.Length > maximumHostLength)
            host = host[..maximumHostLength].TrimEnd('-');
        if (string.IsNullOrEmpty(host)) host = "app";

        return $"{prefix} {host}-{suffix}";
    }

    private static PlatformGitHubAppSummary? Summary(PlatformGitHubAppCredential? value) => value is null
        ? null
        : new PlatformGitHubAppSummary(
            value.Id, value.Kind.ToString(), value.OwnerLogin, value.AppId, value.AppName,
            value.AppSlug, value.InstallUrl, value.Status.ToString(), value.Revision,
            value.VerifiedAt, value.ActivatedAt, value.FailureMessage);

    private static bool Matches(
        PlatformGitHubAppCredential? credential,
        TrustedGitHubAppConfigurationStatus status) =>
        credential is not null && status.Configured && status.AppId == credential.AppId &&
        status.Revision == credential.Revision;

    private static async Task<TrustedGitHubAppConfigurationStatus> SafeStatusAsync(
        Func<CancellationToken, Task<TrustedGitHubAppConfigurationStatus>> action,
        CancellationToken cancellationToken)
    {
        try { return await action(cancellationToken); }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        { return new TrustedGitHubAppConfigurationStatus(false, null, 0, null, null, "Trusted host unavailable."); }
    }

    private static void ActivateCredential(PlatformGitHubAppCredential credential, DateTimeOffset now)
    {
        credential.Status = PlatformGitHubAppCredentialStatus.Active;
        credential.ActivatedAt = now;
        credential.UpdatedAt = now;
    }

    private void Touch(PlatformSourceControlSetupSession session)
    {
        session.UpdatedAt = timeProvider.GetUtcNow();
        session.Revision++;
    }

    private static void RequireRevision(PlatformSourceControlSetupSession session, long expected)
    {
        if (session.Revision != expected)
            throw new DbUpdateConcurrencyException("The setup changed. Refresh before continuing.");
    }

    private static string NormalizeOrganization(string value)
    {
        var result = value.Trim();
        if (result.Length is < 1 or > 39 || result[0] == '-' || result[^1] == '-' ||
            result.Any(x => !char.IsAsciiLetterOrDigit(x) && x != '-'))
            throw new ArgumentException("Enter a valid GitHub organization login.");
        return result;
    }

    private static string NormalizePublicBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.TrimEnd('/'), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("C-Sweet could not determine a safe public application URL.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string SafeFailure(string value) => value.Length <= 2048 ? value : value[..2048];
    private static InvalidOperationException InvalidStep() => new(
        "Complete the current setup step before continuing.");

    private Task WriteAuditAsync(
        PlatformSourceControlSetupSession session,
        Guid userId,
        string eventType,
        string outcome,
        CancellationToken cancellationToken,
        PlatformGitHubAppCredential? credential = null) =>
        audit.AppendAsync(new AuditEventWriteRequest(
            eventType,
            "SourceControl",
            "Inbound",
            outcome,
            EntityType: "PlatformSourceControlSetupSession",
            EntityId: session.Id,
            Summary: "Updated enterprise GitHub App setup.",
            MetadataJson: JsonSerializer.Serialize(new
            {
                session.GitHubOrganization,
                AppKind = credential?.Kind.ToString(),
                credential?.AppId,
                credential?.Revision
            }, JsonOptions),
            Actor: new AuditActor("Human", true, ApplicationUserId: userId)),
            cancellationToken);
}
