using System.Security.Cryptography;
using System.Text;
using CSweet.Application.Communications;
using CSweet.Application.Setup;
using CSweet.Communications.Abstractions;
using CSweet.Contracts.Communications;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Communications;

public sealed class CommunicationWorkspaceService(
    CSweetDbContext db,
    IAuditEventWriter audit,
    IPluginAuthorizationPolicy pluginAuthorization) : ICommunicationWorkspaceService
{
    private static readonly (CommunicationResourceKind Kind, string Purpose, string Name)[] BaseResources =
    [
        (CommunicationResourceKind.Category, "category:start", "C-Sweet • Start Here"),
        (CommunicationResourceKind.Channel, "channel:welcome", "welcome"),
        (CommunicationResourceKind.Channel, "channel:directory", "employee-directory"),
        (CommunicationResourceKind.Channel, "channel:help", "help"),
        (CommunicationResourceKind.Category, "category:executive", "C-Sweet • Executive"),
        (CommunicationResourceKind.Channel, "channel:inbox", "inbox"),
        (CommunicationResourceKind.Channel, "channel:approvals", "approvals"),
        (CommunicationResourceKind.Channel, "channel:decisions", "decisions"),
        (CommunicationResourceKind.Category, "category:teams", "C-Sweet • Teams"),
        (CommunicationResourceKind.Category, "category:projects", "C-Sweet • Projects"),
        (CommunicationResourceKind.Category, "category:agents", "C-Sweet • Agents"),
        (CommunicationResourceKind.Category, "category:operations", "C-Sweet • Operations"),
        (CommunicationResourceKind.Channel, "channel:activity", "activity"),
        (CommunicationResourceKind.Channel, "channel:incidents", "incidents"),
        (CommunicationResourceKind.Category, "category:archive", "C-Sweet • Archive"),
        (CommunicationResourceKind.Role, "role:member", "C-Sweet Member"),
        (CommunicationResourceKind.Role, "role:owner", "C-Sweet Owner")
    ];

    public async Task<CommunicationConnectionResponse?> GetAsync(Guid organizationId, string providerKey, CancellationToken cancellationToken = default) =>
        await db.CommunicationConnections.Where(x => x.OrganizationId == organizationId && x.ProviderKey == NormalizeProviderKey(providerKey))
            .Select(x => ToResponse(x)).SingleOrDefaultAsync(cancellationToken);

    public Task<CommunicationConnectionResponse?> GetDiscordAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        GetAsync(organizationId, CommunicationProviderKeys.Discord, cancellationToken);

    public async Task<CommunicationConnectionResponse> ConnectAsync(Guid organizationId, string providerKey,
        ConnectCommunicationWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        providerKey = NormalizeProviderKey(providerKey);
        if (string.IsNullOrWhiteSpace(request.WorkspaceExternalId) || request.WorkspaceExternalId.Length > 128)
            throw new ArgumentException("A provider workspace identifier is required.", nameof(request));
        if (!Enum.TryParse<CommunicationWorkspaceMode>(request.Mode, true, out var mode))
            throw new ArgumentException("Mode must be Dedicated or Contained.", nameof(request));
        if (!await pluginAuthorization.CanAccessOrganizationAsync(request.PluginInstallationId, organizationId, cancellationToken))
            throw new ArgumentException("The communication plugin is not authorized for this organization.", nameof(request));
        var now = DateTimeOffset.UtcNow;
        var connection = await db.CommunicationConnections.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.ProviderKey == providerKey, cancellationToken);
        if (connection is null)
        {
            connection = new CommunicationConnection
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, ProviderKey = providerKey,
                CreatedAt = now
            };
            db.CommunicationConnections.Add(connection);
        }
        connection.WorkspaceExternalId = request.WorkspaceExternalId.Trim();
        connection.WorkspaceMode = mode;
        connection.Status = CommunicationConnectionStatus.Pending;
        connection.PluginInstallationId = request.PluginInstallationId;
        connection.ManagedRootExternalId = string.IsNullOrWhiteSpace(request.ManagedRootExternalId) ? null : request.ManagedRootExternalId.Trim();
        connection.UpdatedAt = now;
        Queue(connection, CommunicationDeliveryKind.ReconcileWorkspace,
            $"workspace:{connection.Id:D}:connect:{now.ToUnixTimeMilliseconds()}", "{}");
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("communications.provider.connected", "CommunicationConnection", connection.Id,
            $"Provider '{providerKey}' workspace '{connection.WorkspaceExternalId}' connected in {mode} mode.", cancellationToken: cancellationToken);
        return ToResponse(connection);
    }

    public async Task<CommunicationConnectionResponse> ConnectDiscordAsync(Guid organizationId, ConnectDiscordWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        if (!ulong.TryParse(request.GuildId, out _)) throw new ArgumentException("GuildId must be a Discord snowflake.", nameof(request));
        return await ConnectAsync(organizationId, CommunicationProviderKeys.Discord,
            new(request.GuildId, request.Mode, request.PluginInstallationId, request.ManagedRootExternalId), cancellationToken);
    }

    public Task<WorkspaceProvisioningPlan?> PreviewAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        PreviewAsync(organizationId, CommunicationProviderKeys.Discord, cancellationToken);

    public async Task<WorkspaceProvisioningPlan?> PreviewAsync(Guid organizationId, string providerKey, CancellationToken cancellationToken = default)
    {
        var connection = await db.CommunicationConnections.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.ProviderKey == NormalizeProviderKey(providerKey), cancellationToken);
        if (connection is null) return null;
        var existing = await db.ManagedExternalResources.Where(x => x.ConnectionId == connection.Id)
            .ToDictionaryAsync(x => x.Purpose, cancellationToken);
        var desired = new List<(CommunicationResourceKind Kind, string Purpose, string Name)>(BaseResources);
        var agents = await db.CoreOrganizationUsers
            .Where(x => x.OrganizationId == organizationId && x.EmployeeType == EmployeeType.Agent && x.IsActive)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new { x.Id, x.DisplayName })
            .ToListAsync(cancellationToken);
        desired.AddRange(agents.SelectMany(x => new[]
            {
                new ValueTuple<CommunicationResourceKind, string, string>(CommunicationResourceKind.Channel, $"agent:{x.Id}:channel", Slug(x.DisplayName)),
                new ValueTuple<CommunicationResourceKind, string, string>(CommunicationResourceKind.Role, $"agent:{x.Id}:role", $"Agent • {x.DisplayName}"),
                new ValueTuple<CommunicationResourceKind, string, string>(CommunicationResourceKind.Webhook, $"agent:{x.Id}:webhook", x.DisplayName)
            }));
        var groups = await db.CoreConversations
            .Where(x => x.OrganizationId == organizationId &&
                ((x.Kind == ConversationKind.Team && x.TeamId != null) || (x.Kind == ConversationKind.Project && x.WorkstreamId != null)))
            .Select(x => new { x.Kind, x.TeamId, x.WorkstreamId, x.Title }).Distinct().ToListAsync(cancellationToken);
        desired.AddRange(groups.SelectMany(x =>
        {
            var prefix = x.Kind == ConversationKind.Team ? "team" : "project";
            var id = x.TeamId ?? x.WorkstreamId!.Value;
            var name = Slug(x.Title ?? $"{prefix}-{id.ToString("N")[..8]}");
            return new[]
            {
                new ValueTuple<CommunicationResourceKind, string, string>(CommunicationResourceKind.Channel, $"{prefix}:{id}:channel", name),
                new ValueTuple<CommunicationResourceKind, string, string>(CommunicationResourceKind.Webhook, $"{prefix}:{id}:webhook", $"C-Sweet {prefix}")
            };
        }));
        var changes = desired.Select(item => existing.TryGetValue(item.Purpose, out var resource)
            ? new WorkspaceProvisioningChange(resource.DisplayName == item.Name ? CommunicationChangeKind.NoChange : CommunicationChangeKind.Update,
                item.Kind, item.Purpose, item.Name, resource.ExternalId)
            : new WorkspaceProvisioningChange(CommunicationChangeKind.Create, item.Kind, item.Purpose, item.Name)).ToList();
        var desiredPurposes = desired.Select(x => x.Purpose).ToHashSet(StringComparer.Ordinal);
        changes.AddRange(existing.Values.Where(x => !x.IsArchived && !desiredPurposes.Contains(x.Purpose))
            .Select(x => new WorkspaceProvisioningChange(CommunicationChangeKind.Archive,
                Enum.Parse<CommunicationResourceKind>(x.Kind.ToString()), x.Purpose, x.DisplayName, x.ExternalId,
                "Managed resources are archived, never automatically deleted.")));
        return new WorkspaceProvisioningPlan(organizationId, connection.ProviderKey, connection.WorkspaceExternalId, changes, DateTimeOffset.UtcNow);
    }

    public Task<CommunicationActionResponse> QueueReconciliationAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        QueueReconciliationAsync(organizationId, CommunicationProviderKeys.Discord, cancellationToken);

    public async Task<CommunicationActionResponse> QueueReconciliationAsync(Guid organizationId, string providerKey, CancellationToken cancellationToken = default)
    {
        var connection = await db.CommunicationConnections.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.ProviderKey == NormalizeProviderKey(providerKey), cancellationToken);
        if (connection is null) return new(false, "not_connected", "The communication provider is not connected.");
        var now = DateTimeOffset.UtcNow;
        Queue(connection, CommunicationDeliveryKind.ReconcileWorkspace, $"workspace:{connection.Id:D}:reconcile:{now.ToUnixTimeMilliseconds()}", "{}");
        await db.SaveChangesAsync(cancellationToken);
        return new(true, null, "Workspace reconciliation queued.");
    }

    public Task<CommunicationActionResponse> DisconnectDiscordAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        DisconnectAsync(organizationId, CommunicationProviderKeys.Discord, cancellationToken);

    public async Task<CommunicationActionResponse> DisconnectAsync(Guid organizationId, string providerKey, CancellationToken cancellationToken = default)
    {
        providerKey = NormalizeProviderKey(providerKey);
        var connection = await db.CommunicationConnections.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.ProviderKey == providerKey, cancellationToken);
        if (connection is null || connection.Status == CommunicationConnectionStatus.Disconnected)
            return new(false, "not_connected", "The communication provider is not connected.");
        var now = DateTimeOffset.UtcNow;
        connection.Status = CommunicationConnectionStatus.Paused;
        connection.UpdatedAt = now;
        Queue(connection, CommunicationDeliveryKind.DisconnectWorkspace, $"workspace:{connection.Id:D}:disconnect:{now.ToUnixTimeMilliseconds()}", "{}");
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("communications.provider.disconnect_queued", "CommunicationConnection", connection.Id,
            $"Provider '{providerKey}' workspace disconnection and managed-resource archival queued.", cancellationToken: cancellationToken);
        return new(true, null, "Provider disconnection queued. C-Sweet conversation history will be preserved.");
    }

    public async Task<LinkCodeResponse?> CreateLinkCodeAsync(Guid organizationId, Guid applicationUserId, CancellationToken cancellationToken = default)
    {
        var connection = await db.CommunicationConnections.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.ProviderKey == CommunicationProviderKeys.Discord, cancellationToken);
        var member = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId && x.EmployeeType == EmployeeType.Human && x.IsActive, cancellationToken);
        if (connection is null || member is null) return null;
        var bytes = RandomNumberGenerator.GetBytes(6);
        var code = Convert.ToHexString(bytes);
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(10);
        db.ExternalIdentityLinkCodes.Add(new ExternalIdentityLinkCode
        {
            Id = Guid.NewGuid(), ConnectionId = connection.Id, OrganizationId = organizationId,
            ApplicationUserId = applicationUserId, OrganizationUserId = member.Id,
            CodeHash = HashCodeValue(code), CreatedAt = now, ExpiresAt = expiresAt
        });
        Queue(connection, CommunicationDeliveryKind.RegisterLinkCode,
            $"link-code:{connection.Id:D}:{HashCodeValue(code)}",
            System.Text.Json.JsonSerializer.Serialize(new CommunicationPluginLinkCodeRequest(code, expiresAt)));
        await db.SaveChangesAsync(cancellationToken);
        return new(code, expiresAt);
    }

    public async Task<ExternalIdentityLinkResponse?> RedeemLinkCodeAsync(RedeemExternalIdentityRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var hash = HashCodeValue(request.Code.Trim());
        var code = await db.ExternalIdentityLinkCodes.SingleOrDefaultAsync(x => x.CodeHash == hash && x.RedeemedAt == null && x.ExpiresAt > now, cancellationToken);
        if (code is null) return null;
        var connection = await db.CommunicationConnections.SingleOrDefaultAsync(x => x.Id == code.ConnectionId && x.WorkspaceExternalId == request.GuildId, cancellationToken);
        if (connection is null) return null;
        if (connection.PluginInstallationId is Guid pluginId)
        {
            var externalIdentity = await db.ExternalIdentities.SingleOrDefaultAsync(x =>
                x.PluginInstallationId == pluginId && x.ProviderKey == connection.ProviderKey &&
                x.ExternalUserId == request.ExternalUserId, cancellationToken);
            if (externalIdentity is null)
            {
                db.ExternalIdentities.Add(new ExternalIdentity
                {
                    Id = Guid.NewGuid(), PluginInstallationId = pluginId, ProviderKey = connection.ProviderKey,
                    ExternalUserId = request.ExternalUserId, ApplicationUserId = code.ApplicationUserId,
                    CreatedAt = now
                });
            }
            else
            {
                externalIdentity.ApplicationUserId = code.ApplicationUserId;
                externalIdentity.RevokedAt = null;
            }
        }
        var link = await db.ExternalIdentityLinks.SingleOrDefaultAsync(x => x.ConnectionId == connection.Id && x.OrganizationUserId == code.OrganizationUserId, cancellationToken);
        if (link is null)
        {
            link = new ExternalIdentityLink
            {
                Id = Guid.NewGuid(), ConnectionId = connection.Id, OrganizationId = code.OrganizationId,
                ApplicationUserId = code.ApplicationUserId, OrganizationUserId = code.OrganizationUserId,
                ExternalUserId = request.ExternalUserId, IsVerified = true, CreatedAt = now
            };
            db.ExternalIdentityLinks.Add(link);
        }
        else
        {
            link.ExternalUserId = request.ExternalUserId; link.IsVerified = true; link.RevokedAt = null;
        }
        code.RedeemedAt = now;
        var member = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == code.OrganizationUserId, cancellationToken);
        var rolePurpose = member.PermissionLevel == OrganizationPermissionLevel.Owner ? "role:owner" : "role:member";
        var roleId = await db.ManagedExternalResources.Where(x => x.ConnectionId == connection.Id && x.Purpose == rolePurpose && !x.IsArchived)
            .Select(x => x.ExternalId).SingleOrDefaultAsync(cancellationToken);
        if (roleId is not null)
        {
            Queue(connection, CommunicationDeliveryKind.AssignIdentity,
                $"identity:{connection.Id:D}:{request.ExternalUserId}:{roleId}",
                System.Text.Json.JsonSerializer.Serialize(new CommunicationPluginIdentityRequest(
                    connection.WorkspaceExternalId, request.ExternalUserId, roleId)));
        }
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(link);
    }

    public async Task<CommunicationActionResponse> SelectDirectAgentAsync(Guid organizationId, Guid applicationUserId, Guid? agentOrganizationUserId, CancellationToken cancellationToken = default)
    {
        var link = await db.ExternalIdentityLinks.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId && x.RevokedAt == null, cancellationToken);
        if (link is null) return new(false, "identity_not_linked", "Link a Discord identity first.");
        if (agentOrganizationUserId.HasValue && !await db.CoreOrganizationUsers.AnyAsync(x => x.Id == agentOrganizationUserId &&
            x.OrganizationId == organizationId && x.EmployeeType == EmployeeType.Agent && x.IsActive, cancellationToken))
            return new(false, "agent_unavailable", "The selected employee is not an active agent.");
        link.ActiveDirectAgentOrganizationUserId = agentOrganizationUserId;
        await db.SaveChangesAsync(cancellationToken);
        return new(true, null, agentOrganizationUserId.HasValue ? "Direct-message employee selected." : "Direct-message selection cleared.");
    }

    private void Queue(CommunicationConnection connection, CommunicationDeliveryKind kind, string key, string payload)
    {
        var now = DateTimeOffset.UtcNow;
        db.CommunicationDeliveries.Add(new CommunicationDelivery
        {
            Id = Guid.NewGuid(), OrganizationId = connection.OrganizationId, ConnectionId = connection.Id,
            Kind = kind, Status = CommunicationDeliveryStatus.Pending, IdempotencyKey = key, PayloadJson = payload,
            NextAttemptAt = now, CreatedAt = now, UpdatedAt = now
        });
    }

    private static string HashCodeValue(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.ToUpperInvariant())));
    private static string NormalizeProviderKey(string providerKey)
    {
        var value = providerKey?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw new ArgumentException("Provider keys may contain only letters, digits, '.', '-' and '_'.", nameof(providerKey));
        return value;
    }
    private static string Slug(string value)
    {
        var slug = new string(value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        if (slug.Length == 0) slug = "employee";
        return slug[..Math.Min(90, slug.Length)];
    }
    private static CommunicationConnectionResponse ToResponse(CommunicationConnection x) => new(x.Id, x.OrganizationId, x.ProviderKey,
        x.WorkspaceExternalId, x.WorkspaceMode.ToString(), x.Status.ToString(), x.CreatedAt, x.UpdatedAt)
    {
        PluginInstallationId = x.PluginInstallationId,
        ManagedRootExternalId = x.ManagedRootExternalId
    };
    private static ExternalIdentityLinkResponse ToResponse(ExternalIdentityLink x) => new(x.Id, x.OrganizationUserId, x.ExternalUserId, x.IsVerified, x.ActiveDirectAgentOrganizationUserId);
}

public sealed class NotificationService(CSweetDbContext db) : INotificationService
{
    public async Task<IReadOnlyList<NotificationResponse>> ListAsync(Guid organizationId, Guid applicationUserId, CancellationToken cancellationToken = default)
    {
        var recipientIds = db.CoreOrganizationUsers.Where(x => x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId).Select(x => x.Id);
        return await db.UserNotifications.Where(x => x.OrganizationId == organizationId && recipientIds.Contains(x.RecipientOrganizationUserId))
            .OrderByDescending(x => x.CreatedAt).Select(x => new NotificationResponse(x.Id, x.OrganizationId, x.RecipientOrganizationUserId,
                x.OriginatingAgentOrganizationUserId, x.Severity.ToString(), x.Category, x.Title, x.Body, x.ActionUri,
                x.CreatedAt, x.ReadAt, x.DismissedAt)).ToListAsync(cancellationToken);
    }

    public async Task<bool> MarkReadAsync(Guid organizationId, Guid applicationUserId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        var notification = await db.UserNotifications.SingleOrDefaultAsync(x => x.Id == notificationId && x.OrganizationId == organizationId &&
            db.CoreOrganizationUsers.Any(u => u.Id == x.RecipientOrganizationUserId && u.ApplicationUserId == applicationUserId), cancellationToken);
        if (notification is null) return false;
        notification.ReadAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
