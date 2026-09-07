using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Durable, deduplicated wake delivery. Event content is never an execution authorization.</summary>
public sealed class ConnectorActionEventDispatcher(CSweetDbContext db, AgentWorkInbox inbox)
{
    public const string DeliveredKind = "host-connector-action-event-delivered";
    private const string ApprovalRequested = "com.csweet.managed-action.approval-requested.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task DispatchAsync(CancellationToken ct)
    {
        var events = await db.PluginOperationalStates.Where(x => x.Kind == ConnectorActionApprovalService.EventKind)
            .OrderBy(x => x.CreatedAt).Take(25).ToArrayAsync(ct);
        foreach (var item in events)
        {
            using var payload = JsonDocument.Parse(item.PayloadJson);
            var proposalId = payload.RootElement.GetProperty("proposalId").GetGuid();
            var proposal = await db.ActionProposals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == proposalId &&
                x.OrganizationId == item.OrganizationId && x.AgentInstallationId == item.AgentInstallationId &&
                x.ActionType == ConnectorActionApprovalService.ActionType, ct);
            if (proposal is not null)
            {
                var binding = ConnectorActionApprovalService.Parse(proposal);
                var status = payload.RootElement.GetProperty("status").GetString()!;
                if (status == "Requested" && proposal.Status == ProposalStatus.Pending)
                {
                    await new ConnectorApprovalConversationService(db).EnsureAsync(proposal, binding, ct);
                    var approver = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
                        x.Id == binding.ApproverOrganizationUserId && x.OrganizationId == item.OrganizationId && x.IsActive, ct);
                    if (approver?.EmployeeType == EmployeeType.Agent && approver.AgentInstallationId is { } target)
                        await EnqueueAsync(item, target, ApprovalRequested, JsonSerializer.SerializeToElement(new
                        {
                            proposalId, actionType = binding.ActionType, binding.ChannelId, binding.ResourceId,
                            binding.PayloadHash, binding.ExpectedRevision, actionIdempotencyKey = binding.IdempotencyKey,
                            decisionCapability = CapabilityNames.Platform.ManagedActionDecide, binding.ReviewPayload, binding.ExpiresAt
                        }, Json), ct);
                }
                await EnqueueAsync(item, item.AgentInstallationId, ConnectorActionEvents.Changed,
                    JsonSerializer.SerializeToElement(new ConnectorActionChanged(proposalId, binding.ActionType,
                        status == "Requested" ? "AwaitingApproval" : status), Json), ct);
            }
            item.Kind = DeliveredKind; item.Revision++; item.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task EnqueueAsync(PluginOperationalState item, Guid target, string name, JsonElement payload, CancellationToken ct)
    {
        var organization = item.OrganizationId.ToString("D");
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant).Include(x => x.PackageVersion)
            .SingleOrDefaultAsync(x => x.Id == target && x.BusinessId == organization && x.IsEnabled &&
                x.SetupState == PluginSetupState.Ready && x.RevisionStatus == PluginRevisionStatus.Active, ct);
        if (installation?.Grant is null) return;
        var manifest = JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion!.ManifestJson, Json)!;
        if (!manifest.Events.Subscribes.Contains(name, StringComparer.Ordinal) ||
            !(JsonSerializer.Deserialize<string[]>(installation.Grant.EventSubscriptionsJson) ?? []).Contains(name, StringComparer.Ordinal)) return;
        await inbox.EnqueueAsync(organization, target, CSweet.Domain.Setup.AgentWorkKind.Event, name, payload,
            $"connector-action:{item.Id:N}:{name}", DateTimeOffset.UtcNow.AddDays(7),
            correlationId: (payload.TryGetProperty("actionId", out var action) ? action : payload.GetProperty("proposalId")).GetGuid().ToString("D"),
            sourceType: "connector-action", sourceId: item.Id.ToString("D"), cancellationToken: ct);
    }
}
