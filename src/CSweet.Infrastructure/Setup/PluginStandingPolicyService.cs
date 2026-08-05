using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class PluginStandingPolicyService(CSweetDbContext db, IAuditEventWriter audit)
    : IPluginStandingPolicyService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
        { "Publishing", "ContentManagement", "CommentReplies", "Moderation", "LiveConfiguration", "Engagement" };
    private static readonly HashSet<string> PrivacyValues = new(StringComparer.Ordinal)
        { "private", "unlisted", "public" };
    private static readonly HashSet<string> HardGateActions = new(StringComparer.Ordinal)
    {
        "delete-permanently", "playlist-delete", "caption-delete", "ban-user", "go-live",
        "content-id-claim", "content-id-policy",
        "ownership-change", "monetization-change", "ad-change"
    };

    public async Task<PluginStandingPolicyResponse?> GetAsync(Guid organizationId, Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var entity = await db.PluginStandingPolicies.AsNoTracking().Where(x =>
                x.OrganizationId == organizationId && x.AgentInstallationId == installationId)
            .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<PluginStandingPolicyResponse> ApproveAsync(Guid organizationId, Guid applicationUserId,
        Guid installationId, ApprovePluginStandingPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive &&
            x.PermissionLevel == OrganizationPermissionLevel.Owner, cancellationToken)
            ?? throw new UnauthorizedAccessException("Only an active organization owner may approve a standing policy.");
        var installation = await db.AgentInstallations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == installationId && x.BusinessId == organizationId.ToString("D") &&
            x.SetupState == PluginSetupState.Ready, cancellationToken)
            ?? throw new InvalidOperationException("The ready installation was not found.");
        if (!await db.PluginConnections.AsNoTracking().AnyAsync(x => x.AgentInstallationId == installationId &&
                x.Status == PluginConnectionStatus.Connected && x.BoundResourceId == request.ChannelId,
                cancellationToken))
            throw new InvalidOperationException("The standing policy channel is not the installation's confirmed channel.");
        Validate(request.Policy);
        var previous = await db.PluginStandingPolicies.Where(x => x.OrganizationId == organizationId &&
                x.AgentInstallationId == installationId).OrderByDescending(x => x.Revision)
            .FirstOrDefaultAsync(cancellationToken);
        if (request.ExpectedRevision.HasValue && request.ExpectedRevision != previous?.Revision)
            throw new InvalidOperationException("The standing policy changed after it was loaded.");
        if (previous?.Status == PluginStandingPolicyStatus.Approved)
        {
            previous.Status = PluginStandingPolicyStatus.Revoked;
            previous.RevokedAt = DateTimeOffset.UtcNow;
        }
        var policyJson = JsonSerializer.Serialize(request.Policy, JsonOptions);
        var entity = new PluginStandingPolicy
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = installationId,
            ChannelId = request.ChannelId, PolicyJson = policyJson, PayloadHash = Hash(policyJson),
            Revision = (previous?.Revision ?? 0) + 1, Status = PluginStandingPolicyStatus.Approved,
            ApprovedByOrganizationUserId = owner.Id, CreatedAt = DateTimeOffset.UtcNow,
            ApprovedAt = DateTimeOffset.UtcNow
        };
        db.PluginStandingPolicies.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("plugin-standing-policy.approved", nameof(PluginStandingPolicy), entity.Id,
            $"Owner approved standing policy revision {entity.Revision} for channel {entity.ChannelId}.",
            JsonSerializer.Serialize(new { organizationId, installationId, entity.ChannelId, entity.PayloadHash,
                entity.Revision, owner = owner.Id }), cancellationToken);
        return Map(entity);
    }

    public async Task RevokeAsync(Guid organizationId, Guid applicationUserId, Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive &&
            x.PermissionLevel == OrganizationPermissionLevel.Owner, cancellationToken)
            ?? throw new UnauthorizedAccessException("Only an active organization owner may revoke a standing policy.");
        var entity = await db.PluginStandingPolicies.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.AgentInstallationId == installationId && x.Status == PluginStandingPolicyStatus.Approved,
            cancellationToken);
        if (entity is null) return;
        entity.Status = PluginStandingPolicyStatus.Revoked;
        entity.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("plugin-standing-policy.revoked", nameof(PluginStandingPolicy), entity.Id,
            $"Owner revoked standing policy revision {entity.Revision}.",
            JsonSerializer.Serialize(new { organizationId, installationId, owner = owner.Id }), cancellationToken);
    }

    public async Task<ManagedActionPolicyDecision> EvaluateAsync(ManagedActionPolicyInput input,
        CancellationToken cancellationToken = default)
    {
        if (HardGateActions.Contains(input.ActionType))
            return new(false, Reason: "This action always requires approval.");
        var configurationJson = await db.AgentInstallationConfigurations.AsNoTracking()
            .Where(x => x.AgentInstallationId == input.InstallationId).Select(x => x.SettingsJson)
            .SingleOrDefaultAsync(cancellationToken);
        if (!IsFullyAutonomous(configurationJson))
            return new(false, Reason: "The installation is not in Fully Autonomous mode.");
        var policy = await db.PluginStandingPolicies.SingleOrDefaultAsync(x =>
            x.OrganizationId == input.OrganizationId && x.AgentInstallationId == input.InstallationId &&
            x.ChannelId == input.ChannelId && x.Status == PluginStandingPolicyStatus.Approved,
            cancellationToken);
        if (policy is null) return new(false, Reason: "No active owner-approved standing policy exists.");
        var definition = JsonSerializer.Deserialize<PluginStandingPolicyDefinition>(policy.PolicyJson, JsonOptions)
            ?? throw new InvalidOperationException("The approved standing policy is invalid.");
        var category = Category(input.ActionType);
        if (!definition.AllowedActionCategories.Contains(category, StringComparer.Ordinal))
            return new(false, policy.Id, policy.Revision, "The action category is outside the standing policy.");
        if (category == "CommentReplies" && !definition.AllowReplies ||
            category == "Moderation" && !definition.AllowModeration)
            return new(false, policy.Id, policy.Revision, "The action is disabled by the standing policy.");
        var raw = input.Payload.GetRawText();
        if (definition.EscalationKeywords.Any(x => raw.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return new(false, policy.Id, policy.Revision, "The payload matched a standing-policy escalation condition.");
        if (FindString(input.Payload, "privacyStatus") is { } privacy &&
            !definition.AllowedPrivacyValues.Contains(privacy, StringComparer.Ordinal))
            return new(false, policy.Id, policy.Revision, "The requested privacy is outside the standing policy.");
        var scheduled = FindString(input.Payload, "publishAt") is { } publishAt &&
                        DateTimeOffset.TryParse(publishAt, out var parsed) ? parsed : DateTimeOffset.UtcNow;
        if (!definition.AllowedUtcDays.Contains((int)scheduled.UtcDateTime.DayOfWeek) ||
            scheduled.UtcDateTime.Hour < definition.AllowedUtcStartHour ||
            scheduled.UtcDateTime.Hour >= definition.AllowedUtcEndHour)
            return new(false, policy.Id, policy.Revision, "The requested schedule is outside the standing policy.");
        var existing = await db.PluginOperationalStates.SingleOrDefaultAsync(x =>
            x.AgentInstallationId == input.InstallationId && x.Kind == "autonomous-action" &&
            x.ExternalKey == input.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            using var stored = JsonDocument.Parse(existing.PayloadJson);
            return stored.RootElement.GetProperty("payloadHash").GetString() == input.PayloadHash
                ? new(true, policy.Id, policy.Revision)
                : new(false, policy.Id, policy.Revision, "The idempotency key is bound to different content.");
        }
        var since = DateTimeOffset.UtcNow.AddHours(-1);
        var count = await db.PluginOperationalStates.CountAsync(x => x.AgentInstallationId == input.InstallationId &&
            x.Kind == "autonomous-action" && x.UpdatedAt >= since, cancellationToken);
        if (count >= definition.MaximumActionsPerHour)
            return new(false, policy.Id, policy.Revision, "The standing-policy hourly rate limit was reached.");
        var now = DateTimeOffset.UtcNow;
        db.PluginOperationalStates.Add(new PluginOperationalState
        {
            Id = Guid.NewGuid(), OrganizationId = input.OrganizationId, AgentInstallationId = input.InstallationId,
            Kind = "autonomous-action", ExternalKey = input.IdempotencyKey,
            PayloadJson = JsonSerializer.Serialize(new { input.PayloadHash, input.ActionType, input.ChannelId,
                policyId = policy.Id, policyRevision = policy.Revision }, JsonOptions), CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("plugin-standing-policy.action-authorized", nameof(PluginStandingPolicy), policy.Id,
            $"Standing policy authorized {input.ActionType} on channel {input.ChannelId}.",
            JsonSerializer.Serialize(new { input.OrganizationId, input.InstallationId, input.PayloadHash,
                input.IdempotencyKey, policy.Revision }), cancellationToken);
        return new(true, policy.Id, policy.Revision);
    }

    private static bool IsFullyAutonomous(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(json ?? "{}");
            return document.RootElement.TryGetProperty("approvalMode", out var mode) &&
                   mode.GetString() == "Fully Autonomous";
        }
        catch (JsonException) { return false; }
    }

    private static string Category(string action) => action switch
    {
        "publish" => "Publishing",
        "reply" or "chat-send" => "CommentReplies",
        "moderate" => "Moderation",
        "create" or "update" or "create-stream" or "update-stream" or "bind" => "LiveConfiguration",
        _ when action.StartsWith("playlist-", StringComparison.Ordinal) ||
               action.StartsWith("caption-", StringComparison.Ordinal) || action == "set-thumbnail" => "ContentManagement",
        _ => "Engagement"
    };

    private static string? FindString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
                var nested = FindString(property.Value, name);
                if (nested is not null) return nested;
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, name);
                if (nested is not null) return nested;
            }
        return null;
    }

    private static void Validate(PluginStandingPolicyDefinition policy)
    {
        if (policy.AllowedActionCategories.Count == 0 ||
            policy.AllowedActionCategories.Except(Categories, StringComparer.Ordinal).Any())
            throw new ArgumentException("Choose one or more recognized action categories.");
        if (policy.AllowedPrivacyValues.Count == 0 ||
            policy.AllowedPrivacyValues.Except(PrivacyValues, StringComparer.Ordinal).Any())
            throw new ArgumentException("Choose one or more recognized privacy values.");
        if (policy.AllowedUtcDays.Count == 0 || policy.AllowedUtcDays.Any(x => x is < 0 or > 6) ||
            policy.AllowedUtcStartHour is < 0 or > 23 || policy.AllowedUtcEndHour is < 1 or > 24 ||
            policy.AllowedUtcStartHour >= policy.AllowedUtcEndHour || policy.MaximumActionsPerHour is < 1 or > 1000)
            throw new ArgumentException("The schedule or hourly action limit is invalid.");
        if (policy.EscalationKeywords.Count > 50 || policy.EscalationKeywords.Any(x =>
                string.IsNullOrWhiteSpace(x) || x.Length > 80))
            throw new ArgumentException("Escalation keywords must be non-empty, bounded, and limited to 50 entries.");
    }

    private static PluginStandingPolicyResponse Map(PluginStandingPolicy value) => new(
        value.Id, value.AgentInstallationId, value.ChannelId,
        JsonSerializer.Deserialize<PluginStandingPolicyDefinition>(value.PolicyJson, JsonOptions)!,
        value.PayloadHash, value.Revision, value.Status.ToString(), value.ApprovedByOrganizationUserId,
        value.ApprovedAt, value.RevokedAt);
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
