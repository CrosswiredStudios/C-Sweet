using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>
/// Host-only owner policy governance and exact-plan reservations. A reservation is not an executable
/// action decision: the action approval boundary must explicitly consume and revalidate it.
/// </summary>
public sealed class ConnectorStandingPolicyService(CSweetDbContext db, ConnectorPlanService plans,
    IAuditEventWriter audit, TimeProvider? clock = null)
{
    public const string PolicyKind = "host-connector-standing-policy";
    public const string AuthorizationKind = "host-connector-policy-authorization";
    public const string ExecutionPermitKind = "host-connector-policy-start";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public sealed record Authorization(Guid PolicyId, long PolicyRevision, string PolicyHash,
        Guid OwnerId, Guid PlanId, string PlanHash, DateTimeOffset AuthorizedAt);
    public sealed record ExecutionPermit(Authorization Authorization, DateTimeOffset StartedAt);
    public sealed record StoredPolicy(Guid Id, long PolicyRevision, string Status, string PolicyHash,
        string AuthorityHash, string ApprovalRequestHash, Guid OwnerId, DateTimeOffset ApprovedAt, ConnectorStandingPolicyDefinition Definition,
        IReadOnlyList<DateTimeOffset> Reservations);
    private sealed record Authority(FrozenConnectorPlan Plan, PluginProviderOperationDeclaration Operation,
        string Hash, string Description, string AccountName, string RequesterName);

    public async Task<ConnectorStandingPolicyReview> ReviewAsync(Guid organizationId, Guid requesterId,
        Guid applicationUserId, Guid planId, string planHash, CancellationToken ct)
    {
        _ = await Owner(organizationId, applicationUserId, ct);
        var authority = await Inspect(organizationId, requesterId, planId, planHash, ct, requireExecutable: false);
        return new(planId, planHash, ReviewHash(authority, planId, planHash), authority.Description,
            authority.AccountName, ConnectorStandingPolicyRules.Fields(authority.Operation),
            ConnectorStandingPolicyRules.CanAuthorize(authority.Operation))
        {
            RequesterName = authority.RequesterName,
            FieldReviews = ConnectorStandingPolicyProjection.Fields(authority.Operation, authority.Plan),
            Media = authority.Plan.Media is { } media ? new(media.FileName ?? "Attached media", media.SizeBytes, media.ContentType) : null
        };
    }

    public async Task<ConnectorStandingPolicyView> ApproveAsync(Guid organizationId, Guid requesterId,
        Guid applicationUserId, ApproveConnectorStandingPolicyRequest request, CancellationToken ct)
    {
        var owner = await Owner(organizationId, applicationUserId, ct);
        var authority = await Inspect(organizationId, requesterId, request.TemplatePlanId, request.PlanHash, ct, requireExecutable: false);
        if (request.ReviewHash != ReviewHash(authority, request.TemplatePlanId, request.PlanHash))
            throw new UnauthorizedAccessException("Review the current operation, account and grants before approving a policy.");
        var now = Now;
        ConnectorStandingPolicyRules.Validate(request.Definition, authority.Operation, now);
        var record = await Record(organizationId, requesterId, authority.Plan.Capability, ct);
        var previous = record is null ? null : Parse(record);
        var approvalRequestHash = Hash(new { owner = owner.Id, request });
        if (previous?.ApprovalRequestHash == approvalRequestHash) return View(previous);
        if (request.ExpectedRevision != previous?.PolicyRevision)
            throw new InvalidOperationException("The standing policy changed after review.");
        // A new revision never inherits an old authorization or resets its sliding-window usage.
        var policy = new StoredPolicy(Guid.NewGuid(), (previous?.PolicyRevision ?? 0) + 1, "Approved", "",
            authority.Hash, approvalRequestHash, owner.Id, now, request.Definition,
            previous?.Reservations.Where(x => x > now.AddHours(-1)).ToArray() ?? []);
        policy = policy with { PolicyHash = PolicyHash(policy) };
        // Record the owner's exact intent before any authority is made durable. If the policy
        // write fails, this is an attempted approval, not a claim that it became executable.
        await audit.WriteAsync("connector.policy.approval-requested", "ConnectorStandingPolicy", policy.Id,
            "A human organization owner requested an exact reviewed standing policy.",
            JsonSerializer.Serialize(new { organizationId, requesterId, policy.OwnerId, policy.PolicyHash, policy.PolicyRevision }), ct);
        if (record is null)
        {
            record = new() { Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = requesterId,
                Kind = PolicyKind, ExternalKey = authority.Plan.Capability, CreatedAt = now };
            db.PluginOperationalStates.Add(record);
        }
        Store(record, policy, now);
        await db.SaveChangesAsync(ct);
        return View(policy);
    }

    public async Task<ConnectorStandingPolicyView?> GetAsync(Guid organizationId, Guid requesterId,
        Guid applicationUserId, string capability, CancellationToken ct)
    {
        _ = await Owner(organizationId, applicationUserId, ct);
        var record = await Record(organizationId, requesterId, capability, ct);
        return record is null ? null : View(Parse(record));
    }

    public async Task RevokeAsync(Guid organizationId, Guid requesterId, Guid applicationUserId,
        string capability, Guid expectedPolicyId, long expectedRevision, CancellationToken ct)
    {
        var owner = await Owner(organizationId, applicationUserId, ct);
        var record = await Record(organizationId, requesterId, capability, ct)
            ?? throw new InvalidOperationException("The standing policy is unavailable.");
        var policy = Parse(record);
        if (policy.Id != expectedPolicyId || policy.PolicyRevision != expectedRevision)
            throw new InvalidOperationException("The standing policy changed after review.");
        if (policy.Status == "Revoked") return;
        Store(record, policy with { Status = "Revoked" }, Now);
        await db.SaveChangesAsync(ct); // Disable first; audit unavailability must never restore permission.
        await audit.WriteAsync("connector.policy.revoked", "ConnectorStandingPolicy", policy.Id,
            "A human organization owner revoked a standing policy.",
            JsonSerializer.Serialize(new { organizationId, requesterId, actor = owner.Id, policy.PolicyHash, policy.PolicyRevision }), ct);
    }

    public async Task<Authorization?> TryReserveAsync(Guid organizationId, Guid requesterId,
        Guid planId, string planHash, CancellationToken ct)
    {
        var authority = await Inspect(organizationId, requesterId, planId, planHash, ct);
        if (!await Autonomous(organizationId, requesterId, ct)) return null;
        var record = await Record(organizationId, requesterId, authority.Plan.Capability, ct);
        if (record is null) return null;
        var policy = Parse(record);
        var now = Now;
        if (!await Matches(policy, authority, organizationId, now, ct)) return null;
        var prior = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == requesterId && x.Kind == AuthorizationKind &&
            x.ExternalKey == planId.ToString("N"), ct);
        if (prior is not null)
        {
            var receipt = JsonSerializer.Deserialize<Authorization>(prior.PayloadJson, Json)!;
            ValidateReceipt(receipt, policy, planId, planHash);
            return receipt;
        }
        var recent = policy.Reservations.Where(x => x > now.AddHours(-1)).ToArray();
        if (recent.Length >= policy.Definition.MaximumActionsPerHour) return null;
        var authorization = new Authorization(policy.Id, policy.PolicyRevision, policy.PolicyHash, policy.OwnerId,
            planId, planHash, now);
        Store(record, policy with { Reservations = [.. recent, now] }, now);
        db.PluginOperationalStates.Add(new() { Id = Guid.NewGuid(), OrganizationId = organizationId,
            AgentInstallationId = requesterId, Kind = AuthorizationKind, ExternalKey = planId.ToString("N"),
            PayloadJson = JsonSerializer.Serialize(authorization, Json), Revision = 1, CreatedAt = now, UpdatedAt = now });
        // Existing operational-state revision is a cross-process optimistic fence. The counter
        // and exact-plan receipt commit together. A race must retry with a fresh DbContext.
        await db.SaveChangesAsync(ct);
        return authorization;
    }

    public async Task RequireAuthorizationAsync(Guid organizationId, Guid requesterId, Guid planId,
        string planHash, Authorization expected, CancellationToken ct)
    {
        var authority = await Inspect(organizationId, requesterId, planId, planHash, ct);
        var record = await Record(organizationId, requesterId, authority.Plan.Capability, ct);
        if (record is null || !await Autonomous(organizationId, requesterId, ct))
            throw new UnauthorizedAccessException("The standing policy is no longer enabled.");
        var policy = Parse(record);
        if (!await Matches(policy, authority, organizationId, Now, ct))
            throw new UnauthorizedAccessException("The plan no longer satisfies its standing policy.");
        ValidateReceipt(expected, policy, planId, planHash);
        var receipt = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == requesterId && x.Kind == AuthorizationKind &&
            x.ExternalKey == planId.ToString("N"), ct);
        if (receipt is null || JsonSerializer.Deserialize<Authorization>(receipt.PayloadJson, Json) != expected)
            throw new UnauthorizedAccessException("The exact standing-policy authorization is missing or modified.");
        var execution = await db.ConnectorExecutions.AsNoTracking().SingleAsync(x => x.Id == planId, ct);
        var permitRecord = await db.PluginOperationalStates.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.AgentInstallationId == requesterId && x.Kind == ExecutionPermitKind && x.ExternalKey == planId.ToString("N"), ct);
        if (permitRecord is not null)
        {
            await db.Entry(permitRecord).ReloadAsync(ct);
            if (db.Entry(permitRecord).State == EntityState.Detached) permitRecord = null;
        }
        var permit = permitRecord is null ? null : JsonSerializer.Deserialize<ExecutionPermit>(permitRecord.PayloadJson, Json);
        if (permit is not null && permit.Authorization != expected)
            throw new UnauthorizedAccessException("The execution permit belongs to another policy authorization.");
        if (execution.Status == "Executing" && permit is null)
            throw new UnauthorizedAccessException("Policy execution must acquire its rate-limited start permit before claiming work.");
        if (execution.Status == "Approved")
        {
            // Approval can wait in a durable queue. Re-meter at the execution claim, not only
            // at preparation, so old approvals cannot burst past a current hourly limit.
            var now = Now;
            var recent = policy.Reservations.Where(x => x > now.AddHours(-1)).ToList();
            recent.Remove(permit?.StartedAt ?? expected.AuthorizedAt);
            if (recent.Count >= policy.Definition.MaximumActionsPerHour)
                throw new UnauthorizedAccessException("The standing policy's current execution rate limit is reached.");
            recent.Add(now);
            Store(record, policy with { Reservations = recent }, now);
            if (permitRecord is null)
            {
                permitRecord = new() { Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = requesterId,
                    Kind = ExecutionPermitKind, ExternalKey = planId.ToString("N"), CreatedAt = now };
                db.PluginOperationalStates.Add(permitRecord);
            }
            permitRecord.PayloadJson = JsonSerializer.Serialize(new ExecutionPermit(expected, now), Json);
            permitRecord.Revision++; permitRecord.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<bool> Matches(StoredPolicy policy, Authority authority, Guid organizationId, DateTimeOffset now, CancellationToken ct)
    {
        if (policy.Status != "Approved" || policy.AuthorityHash != authority.Hash ||
            !ConnectorStandingPolicyRules.CanAuthorize(authority.Operation) ||
            !await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == policy.OwnerId && x.OrganizationId == organizationId &&
                x.IsActive && x.EmployeeType == EmployeeType.Human && x.ApplicationUserId != null && x.PermissionLevel == OrganizationPermissionLevel.Owner, ct)) return false;
        ConnectorStandingPolicyRules.Validate(policy.Definition, authority.Operation, policy.ApprovedAt);
        return ConnectorStandingPolicyRules.Matches(policy.Definition, authority.Plan, now);
    }

    private async Task<Authority> Inspect(Guid organizationId, Guid requesterId, Guid planId, string planHash, CancellationToken ct, bool requireExecutable = true)
    {
        var plan = requireExecutable ? await plans.RevalidateAsync(organizationId, requesterId, planId, planHash, ct)
            : await plans.ValidateResultAuthorityAsync(organizationId, requesterId, planId, planHash, ct);
        var provider = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).SingleAsync(x => x.Id == plan.ConnectorInstallationId, ct);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(provider.PackageVersion!.ManifestJson, Json)!;
        var operation = manifest.ProviderOperations.Single(x => x.Capability == plan.Capability);
        var requester = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).SingleAsync(x => x.Id == requesterId, ct);
        if (string.IsNullOrWhiteSpace(requester.PackageVersion?.PackageDigest))
            throw new UnauthorizedAccessException("Standing policies require the consuming agent's immutable package digest.");
        var connection = await db.PluginConnections.AsNoTracking().SingleAsync(x => x.Id == plan.ConnectionId, ct);
        var binding = await db.AgentCapabilityBindings.AsNoTracking().SingleAsync(x => x.RequesterInstallationId == requesterId &&
            x.ProviderInstallationId == provider.Id && x.Capability == plan.Capability && x.RevokedAt == null, ct);
        var profile = await db.ConnectorProfileApprovals.AsNoTracking().SingleAsync(x => x.ConnectorInstallationId == provider.Id &&
            x.PackageDigest == plan.PackageDigest && x.ProfileId == connection.ProviderProfile && x.RevokedAt == null, ct);
        var hash = Hash(new { plan.OrganizationId, plan.RequesterInstallationId, plan.ConnectorInstallationId, plan.ConnectionId,
            plan.ResourceId, plan.GrantRevision, plan.ProviderGrantRevision, plan.PackageDigest, plan.Capability,
            requesterPackage = requester.PackageVersion!.PackageDigest, consumerBinding = binding.ApprovedAt,
            profileApproval = profile.ApprovedAt, connection.UpdatedAt, operation });
        var requesterName = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            x.AgentInstallationId == requesterId && x.IsActive).Select(x => x.DisplayName).SingleOrDefaultAsync(ct);
        return new(plan, operation, hash, manifest.Provides.Single(x => x.Name == plan.Capability).Description,
            connection.ExternalAccountName ?? "Connected account", string.IsNullOrWhiteSpace(requesterName) ? "Requesting agent" : requesterName);
    }

    private async Task<OrganizationUser> Owner(Guid organizationId, Guid applicationUserId, CancellationToken ct)
    {
        if (applicationUserId == Guid.Empty) throw new UnauthorizedAccessException("Authenticated human ownership is required.");
        return await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId && x.ApplicationUserId != null && x.IsActive &&
            x.EmployeeType == EmployeeType.Human && x.PermissionLevel == OrganizationPermissionLevel.Owner, ct)
        ?? throw new UnauthorizedAccessException("An active human organization owner must review this policy.");
    }

    private async Task<bool> Autonomous(Guid organizationId, Guid requesterId, CancellationToken ct)
    {
        if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.OrganizationId == organizationId &&
            x.AgentInstallationId == requesterId && x.IsActive, ct)) return false;
        var settings = await db.AgentInstallationConfigurations.AsNoTracking().Where(x => x.AgentInstallationId == requesterId)
            .Select(x => x.SettingsJson).SingleOrDefaultAsync(ct);
        using var document = JsonDocument.Parse(settings ?? "{}");
        return document.RootElement.TryGetProperty("approvalMode", out var mode) && mode.GetString() == "Fully Autonomous";
    }

    private async Task<PluginOperationalState?> Record(Guid organizationId, Guid requesterId, string capability, CancellationToken ct)
    {
        var record = await db.PluginOperationalStates.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.AgentInstallationId == requesterId &&
            x.Kind == PolicyKind && x.ExternalKey == capability, ct);
        if (record is null) return null;
        // A transfer may reuse one DbContext. Never let its identity map hide a revocation
        // committed by a different host process between requests or chunks.
        await db.Entry(record).ReloadAsync(ct);
        return db.Entry(record).State == EntityState.Detached ? null : record;
    }
    private static StoredPolicy Parse(PluginOperationalState record)
    {
        var policy = JsonSerializer.Deserialize<StoredPolicy>(record.PayloadJson, Json)
            ?? throw new UnauthorizedAccessException("The standing policy is invalid.");
        if (policy.PolicyHash != PolicyHash(policy)) throw new UnauthorizedAccessException("The standing policy was modified.");
        return policy;
    }
    private static void ValidateReceipt(Authorization receipt, StoredPolicy policy, Guid planId, string planHash)
    {
        if (receipt.PolicyId != policy.Id || receipt.PolicyRevision != policy.PolicyRevision || receipt.PolicyHash != policy.PolicyHash ||
            receipt.OwnerId != policy.OwnerId || receipt.PlanId != planId || receipt.PlanHash != planHash || receipt.AuthorizedAt < policy.ApprovedAt)
            throw new UnauthorizedAccessException("This plan is bound to a different policy or authority revision.");
    }
    private static string PolicyHash(StoredPolicy p) => Hash(new { p.Id, p.PolicyRevision, p.AuthorityHash, p.ApprovalRequestHash, p.OwnerId, p.ApprovedAt, p.Definition });
    private static string ReviewHash(Authority authority, Guid planId, string planHash) => Hash(new { planId, planHash, authority.Hash, authority.Description, authority.AccountName, authority.RequesterName });
    private static string Hash<T>(T value) => ConnectorRequestMaterializer.Hash(JsonSerializer.SerializeToElement(value, Json));
    private static void Store(PluginOperationalState record, StoredPolicy policy, DateTimeOffset now)
    { record.PayloadJson = JsonSerializer.Serialize(policy, Json); record.Revision++; record.UpdatedAt = now; }
    private static ConnectorStandingPolicyView View(StoredPolicy policy) => new(policy.Id, policy.PolicyRevision,
        policy.Status, policy.PolicyHash, policy.OwnerId, policy.ApprovedAt, policy.Definition);

    /// <summary>Stages revocation in the caller's account/grant transaction; never restores it on reconnect.</summary>
    public static async Task RevokeForConsumersAsync(CSweetDbContext db, Guid organizationId,
        IReadOnlyList<Guid> consumerIds, CancellationToken ct)
    {
        var records = await db.PluginOperationalStates.Where(x => x.OrganizationId == organizationId &&
            consumerIds.Contains(x.AgentInstallationId) && x.Kind == PolicyKind).ToArrayAsync(ct);
        foreach (var record in records)
        {
            try
            {
                var policy = Parse(record);
                if (policy.Status == "Approved") Store(record, policy with { Status = "Revoked" }, DateTimeOffset.UtcNow);
            }
            catch (Exception error) when (error is JsonException or UnauthorizedAccessException)
            { /* A malformed/tampered policy already fails closed. It must not obstruct disconnection. */ }
        }
    }
}
