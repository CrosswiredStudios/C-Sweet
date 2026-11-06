using AgentWorkKind = CSweet.Domain.Setup.AgentWorkKind;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Explicit human recovery of a failed lifecycle delivery, never a new hire or grant.</summary>
public sealed class AgentOnboardingRecoveryService(CSweetDbContext db, IDataProtectionProvider protection, TimeProvider clock,
    ConnectorCredentialAuthority? credentials = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ReviewLifetime = TimeSpan.FromMinutes(10);
    private readonly IDataProtector reviews = protection.CreateProtector("CSweet.OnboardingRecovery.Review.v1");
    private sealed record Review(Guid Id, Guid Actor, Guid Organization, Guid Installation,
        string StateHash, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);
    private sealed record State(OrganizationUser Human, AgentInstallation Installation,
        AgentOnboardingEventOutboxItem Event, AgentWorkItem Work, PluginOperationalState? Marker, string Hash);

    public Task<OnboardingRecoveryReview> PreviewAsync(Guid actor, Guid organization, Guid installation, CancellationToken ct) =>
        OnboardingDeliveryFence.RunAsync(db, organization, installation, async () =>
        {
            var state = await LoadAsync(actor, organization, installation, ct);
            RequireTerminal(state);
            var now = clock.GetUtcNow();
            var review = new Review(Guid.NewGuid(), actor, organization, installation, state.Hash, now, now.Add(ReviewLifetime));
            return new OnboardingRecoveryReview(state.Event.Id, state.Work.Id, state.Work.AttemptCount,
                checked(state.Work.AttemptCount + 1), review.ExpiresAt, reviews.Protect(JsonSerializer.Serialize(review, Json)));
        }, ct);

    public Task<OnboardingRecoveryReceipt> RecoverAsync(Guid actor, Guid organization, Guid installation,
        string token, CancellationToken ct)
    {
        Review review;
        try
        {
            if (string.IsNullOrEmpty(token) || token.Length > 12000) throw new CryptographicException();
            review = JsonSerializer.Deserialize<Review>(reviews.Unprotect(token), Json) ?? throw new CryptographicException();
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        { throw new InvalidOperationException("The recovery review is invalid. Open a fresh review."); }
        var now = clock.GetUtcNow();
        if (review.Id == Guid.Empty || review.Actor != actor || review.Organization != organization || review.Installation != installation ||
            review.ExpiresAt <= now || review.IssuedAt > now || review.ExpiresAt - review.IssuedAt != ReviewLifetime)
            throw new InvalidOperationException("The recovery review expired or belongs to another operator or installation.");
        return OnboardingDeliveryFence.RunAsync(db, organization, installation, async () =>
        {
            var state = await LoadAsync(actor, organization, installation, ct);
            if (state.Marker is { } prior && OnboardingRecoveryBudget.Read(prior, state.Work) is { } stored &&
                stored.ActorId == actor && stored.Receipt.RecoveryId == review.Id)
                return stored.Receipt; // An uncertain/repeated response never queues another attempt.
            RequireTerminal(state);
            if (state.Hash != review.StateHash || review.ExpiresAt <= clock.GetUtcNow())
                throw new InvalidOperationException("Onboarding or its authority changed. Open a fresh review.");
            var time = clock.GetUtcNow();
            var requestedAt = new DateTimeOffset(time.UtcTicks - time.UtcTicks % 10, TimeSpan.Zero);
            var receipt = new OnboardingRecoveryReceipt(review.Id, state.Event.Id, state.Work.Id,
                checked(state.Work.AttemptCount + 1), requestedAt, requestedAt.AddHours(1));
            AttachFresh(state.Work, state.Work.Id);
            state.Work.Status = AgentWorkStatus.Pending;
            state.Work.AvailableAt = requestedAt;
            state.Work.DeadlineAt = receipt.DeadlineAt;
            state.Work.MaximumAttempts = checked(state.Work.AttemptCount - state.Work.ResourceReplacementCount - state.Work.MaintenanceInterruptionCount + 1);
            // Keep payload, prior result/error, completion hashes, attempts and mutation keys intact.
            AttachFresh(state.Event, state.Event.Id);
            state.Event.Status = AgentOnboardingEventOutboxStatus.Pending;
            state.Event.NextAttemptAt = requestedAt;
            var marker = state.Marker;
            if (marker is null)
            {
                marker = new() { Id = Guid.NewGuid(), OrganizationId = organization, AgentInstallationId = installation,
                    Kind = OnboardingRecoveryBudget.Kind, ExternalKey = state.Work.Id.ToString("N"), CreatedAt = requestedAt };
                db.PluginOperationalStates.Add(marker);
            }
            else AttachFresh(marker, marker.Id);
            marker.PayloadJson = JsonSerializer.Serialize(new OnboardingRecoveryBudget.Stored(actor,
                state.Installation.PackageVersionId, review.StateHash, receipt));
            marker.Revision++; marker.UpdatedAt = requestedAt;
            db.QueueAudit(new AuditEventWriteRequest("agent.onboarding.recovery.requested", OrganizationId: organization,
                EntityType: nameof(AgentWorkItem), EntityId: state.Work.Id,
                Summary: "A human operator authorized one further attempt of the existing onboarding delivery.",
                MetadataJson: JsonSerializer.Serialize(new { receipt, reviewedStateHash = review.StateHash,
                    packageVersionId = state.Installation.PackageVersionId,
                    packageDigest = state.Installation.PackageVersion!.PackageDigest,
                    grantRevision = state.Installation.Grant!.GrantRevision }), OccurredAt: requestedAt,
                Actor: new("Human", ApplicationUserId: actor, OrganizationUserId: state.Human.Id),
                Employees: [new(state.Event.AgentOrganizationUserId)], EventId: review.Id));
            await db.SaveChangesAsync(ct);
            return receipt;
        }, ct);
    }

    public async Task<OnboardingRecoveryReceipt?> ReadReceiptAsync(Guid actor, Guid organization, Guid installation, CancellationToken ct)
    {
        await RequireOperator(actor, organization, ct);
        if (!await db.AgentInstallations.AnyAsync(x => x.Id == installation && x.BusinessId == organization.ToString("D"), ct))
            throw new UnauthorizedAccessException("The installation is unavailable in this business.");
        var marker = await db.PluginOperationalStates.AsNoTracking().Where(x => x.OrganizationId == organization &&
            x.AgentInstallationId == installation && x.Kind == OnboardingRecoveryBudget.Kind).OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync(ct);
        return marker is null ? null : JsonSerializer.Deserialize<OnboardingRecoveryBudget.Stored>(marker.PayloadJson)?.Receipt;
    }

    private async Task<OrganizationUser> RequireOperator(Guid actor, Guid organization, CancellationToken ct)
    {
        await new PluginAccountAccessService(db).RequireAdministratorAsync(actor, ct);
        if (!await db.CoreOrganizations.AsNoTracking().AnyAsync(x=>x.Id==organization && x.Status==OrganizationStatus.Active,ct))
            throw new UnauthorizedAccessException("The active business is unavailable.");
        return await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.ApplicationUserId == actor &&
            x.OrganizationId == organization && x.EmployeeType == EmployeeType.Human && x.IsActive &&
            x.ArchivedAt == null && x.PermissionLevel >= OrganizationPermissionLevel.Manager, ct)
            ?? throw new UnauthorizedAccessException("An active human business manager must review onboarding recovery.");
    }

    private async Task<State> LoadAsync(Guid actor, Guid organization, Guid installation, CancellationToken ct)
    {
        var human = await RequireOperator(actor, organization, ct);
        var target = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).Include(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == installation && x.BusinessId == organization.ToString("D") &&
                x.Scope == PluginInstallationScope.Organization && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active &&
                x.SetupState == PluginSetupState.Ready, ct) ?? throw new UnauthorizedAccessException("The active agent installation is unavailable.");
        if (target.PackageVersion is not { PluginKind: PluginKind.Agent, Status: AgentPackageVersionStatus.Built } package ||
            string.IsNullOrWhiteSpace(package.PackageDigest) || target.Grant is null)
            throw new InvalidOperationException("The current agent package is not ready.");
        var employee = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organization &&
            x.AgentInstallationId == installation && x.EmployeeType == EmployeeType.Agent && x.IsActive && x.ArchivedAt == null, ct)
            ?? throw new UnauthorizedAccessException("The current agent employee is unavailable.");
        var item = await db.AgentOnboardingEventOutbox.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organization &&
            x.AgentOrganizationUserId == employee.Id, ct) ?? throw new InvalidOperationException("The original onboarding event is unavailable.");
        if (!await db.CoreConversations.AnyAsync(x => x.Id == item.ConversationId && x.OrganizationId == organization &&
            x.AgentOrganizationUserId == employee.Id && x.MergedIntoConversationId == null, ct))
            throw new InvalidOperationException("The original onboarding conversation is unavailable.");
        var key = $"onboarding-event:{item.Id:N}:package:{package.Id:N}:{installation:D}";
        var work = await db.AgentWorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.AgentInstallationId == installation &&
            x.OrganizationId == organization.ToString("D") && x.Kind == AgentWorkKind.Event && x.Name == AgentLifecycleEvents.Onboarded &&
            x.SourceType == "platform-event" && x.SourceId == item.Id.ToString("D") && x.IdempotencyKey == key, ct)
            ?? throw new InvalidOperationException("The original delivery for this package is unavailable.");
        RequireRecoverablePayload(work);
        try
        {
            var bytes = protection.CreateProtector("CSweet.AgentWorkInbox.v1").Unprotect(work.ProtectedPayload);
            var expected = JsonSerializer.SerializeToElement(new AgentOnboardedEvent(organization, employee.Id,
                item.HiringOrganizationUserId, item.ConversationId, item.OccurredAt), Json);
            using var document = JsonDocument.Parse(bytes);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != work.PayloadHash || !JsonElement.DeepEquals(expected, document.RootElement))
                throw new InvalidOperationException("The original onboarding payload changed.");
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        { throw new InvalidOperationException("The original onboarding payload is unavailable."); }
        if (await db.AgentWorkAttempts.AnyAsync(x => x.AgentWorkItemId == work.Id && x.FinishedAt == null, ct))
            throw new InvalidOperationException("Settle the existing onboarding attempt before requesting recovery.");
        await new ConnectorBindingService(db, protection).ValidateRequiredBindingsAsync(target, ct);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(package.ManifestJson, Json) ?? throw new InvalidOperationException("The manifest is unavailable.");
        var approved = JsonSerializer.Deserialize<string[]>(target.Grant.RequiredCapabilitiesJson) ?? [];
        if (!approved.Contains(AgentLifecycleCapabilities.CompleteOnboarding) || !manifest.Requires.Any(x => x.Name == AgentLifecycleCapabilities.CompleteOnboarding))
            throw new InvalidOperationException("The agent has no approved onboarding acknowledgement capability.");
        var bindings = await db.AgentCapabilityBindings.AsNoTracking().Include(x => x.ProviderInstallation!).ThenInclude(x => x.PackageVersion)
            .Include(x => x.ProviderInstallation!).ThenInclude(x => x.Grant)
            .Where(x => x.RequesterInstallationId == installation && x.RevokedAt == null).OrderBy(x => x.Id).ToArrayAsync(ct);
        var authority = new List<object>();
        foreach (var binding in bindings)
        {
            if (binding.OrganizationId != target.BusinessId || binding.GrantRevision != target.Grant.GrantRevision)
                throw new InvalidOperationException("The consuming access review changed.");
            var provider = binding.ProviderInstallation ?? throw new InvalidOperationException("A provider is unavailable.");
            authority.Add(new { binding.Id, binding.DependencyId, binding.Capability, binding.ProviderPackageDigest,
                binding.PluginAccountBusinessGrantId, binding.PluginAccountResourceId, binding.PluginAccountConnectionId,
                provider.PackageVersionId, provider.IsEnabled, provider.RevisionStatus, ProviderGrant = provider.Grant?.GrantRevision,
                provider.PackageVersion!.PackageDigest });
            if (provider.PackageVersion.PluginKind == PluginKind.Connector)
            {
                var providerManifest = JsonSerializer.Deserialize<PluginManifest>(provider.PackageVersion.ManifestJson, Json)!;
                if (providerManifest.Kind != "remote-mcp")
                {
                    var current = await new ConnectorPlanService(db, credentials).CurrentAuthorityAsync(organization, installation, binding.Capability, ct);
                    authority.Add(new { current.Account, current.Credential });
                }
            }
        }
        var marker = await OnboardingRecoveryBudget.FindAsync(db, work, ct);
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            Actor = human.Id, HumanRevision = human.Revision, human.PermissionLevel, Installation = target.Id, target.PackageVersionId,
            package.PackageDigest, package.ManifestJson, target.Grant.GrantRevision, target.Grant.RequiredCapabilitiesJson,
            target.Grant.ProvidedCapabilitiesJson, target.Grant.NetworkAccessJson, target.Grant.EventSubscriptionsJson,
            EmployeeRevision = employee.Revision, item.Id, EventStatus = item.Status, item.DeliveredAt, item.Attempts, item.ConversationId,
            Work = work.Id, WorkStatus = work.Status, work.AttemptCount, work.MaximumAttempts, work.DeadlineAt, work.MemoryErasedAt,
            work.ResourceReplacementCount, work.MaintenanceInterruptionCount, work.PayloadHash, work.ResultHash,
            Payload = Convert.ToHexString(SHA256.HashData(work.ProtectedPayload)),
            Failure = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(work.LastError ?? ""))),
            MarkerRevision = marker?.Revision, MarkerHash = marker is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(marker.PayloadJson))), authority
        }, Json)));
        return new(human, target, item, work, marker, hash);
    }

    private void AttachFresh<T>(T entity, Guid id) where T : class
    {
        var tracked = db.ChangeTracker.Entries<T>().SingleOrDefault(x => Equals(x.Property("Id").CurrentValue, id));
        if (tracked is not null)
        {
            if (tracked.State != EntityState.Unchanged)
                throw new InvalidOperationException("Persist pending onboarding changes before reviewing recovery.");
            tracked.State = EntityState.Detached;
        }
        db.Attach(entity);
    }

    internal static void RequireRecoverablePayload(AgentWorkItem work)
    {
        if (work.MemoryErasedAt is not null || work.ProtectedPayload.Length == 0 || work.ProtectedPayload.Length > 256 * 1024 ||
            work.AttemptCount < 1 || work.AttemptCount >= int.MaxValue || work.ResourceReplacementCount < 0 || work.MaintenanceInterruptionCount < 0 ||
            (long)work.ResourceReplacementCount + work.MaintenanceInterruptionCount > work.AttemptCount)
            throw new InvalidOperationException("This delivery cannot be recovered.");
    }

    private static void RequireTerminal(State state)
    {
        if (state.Event.Status != AgentOnboardingEventOutboxStatus.Failed || state.Event.DeliveredAt is not null ||
            state.Work.Status != AgentWorkStatus.DeadLetter)
            throw new InvalidOperationException("Only the unacknowledged, terminally failed onboarding delivery can be recovered.");
    }
}
