using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>The runtime-facing action surface. Identity and grants always come from authenticated host state.</summary>
public sealed class ConnectorActionService(CSweetDbContext db, ConnectorPlanService plans, ConnectorActionApprovalService approvals)
{
    public async Task<ConnectorAction> RequestAsync(Guid organizationId, Guid requesterId, RequestConnectorAction request, CancellationToken ct)
    {
        await RequireControlAsync(organizationId, requesterId, PlatformCapabilities.ConnectorActionRequest, ct);
        if (string.IsNullOrWhiteSpace(request.Capability) || request.Capability.Length > 200 || request.Input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A declared operation and object input are required.");
        if (request.Input.TryGetProperty("idempotencyKey", out var key) &&
            (key.ValueKind != JsonValueKind.String || key.GetString() != request.IdempotencyKey))
            throw new ArgumentException("The operation and action must use the same idempotency key.");
        var plan = await plans.PrepareAsync(organizationId, requesterId, request.Capability, request.Input, request.IdempotencyKey, ct);
        if (plan.ApprovalId is null)
            _ = await approvals.RequestAsync(organizationId, requesterId, plan.Id, plan.PlanHash, ct);
        return await SnapshotAsync(organizationId, requesterId, plan, ct);
    }

    public async Task<ConnectorAction> ReadAsync(Guid organizationId, Guid requesterId, ReadConnectorAction request, CancellationToken ct)
    {
        await RequireControlAsync(organizationId, requesterId, PlatformCapabilities.ConnectorActionRead, ct);
        var plan = await db.ConnectorExecutions.AsNoTracking().SingleOrDefaultAsync(x => x.ApprovalId == request.ActionId &&
            x.OrganizationId == organizationId && x.RequesterInstallationId == requesterId, ct)
            ?? throw new UnauthorizedAccessException("This action does not belong to this installation.");
        return await SnapshotAsync(organizationId, requesterId, plan, ct);
    }

    private async Task<ConnectorAction> SnapshotAsync(Guid organizationId, Guid requesterId, ConnectorExecution plan, CancellationToken ct)
    {
        var status = plan.Status; JsonElement? result = null; string? condition = null;
        if (status != "Cancelled")
        {
            try { _ = await plans.ValidateResultAuthorityAsync(organizationId, requesterId, plan.Id, plan.PlanHash, ct); }
            catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException or JsonException)
            { return new(plan.ApprovalId!.Value, plan.Capability, "Unavailable", plan.UpdatedAt, null, "authority_changed"); }
        }
        if (status is "Prepared" or "AwaitingApproval" or "Approved" && plan.ExpiresAt <= DateTimeOffset.UtcNow)
        { status = "Expired"; condition = "approval_expired"; }
        if (status == "Completed" && plan.ResultJson is { } stored)
            result = JsonSerializer.Deserialize<JsonElement>(stored);
        if (status == "Indeterminate") condition = "reconciliation_required";
        if (status == "Blocked") condition = "intervention_required";
        return new(plan.ApprovalId!.Value, plan.Capability, status, plan.UpdatedAt, result, condition);
    }

    private async Task RequireControlAsync(Guid organizationId, Guid requesterId, string capability, CancellationToken ct)
    {
        var organization = organizationId.ToString("D");
        var grants = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == requesterId && x.BusinessId == organization &&
            x.IsEnabled && x.SetupState == PluginSetupState.Ready && x.RevisionStatus == PluginRevisionStatus.Active)
            .Select(x => x.Grant!.RequiredCapabilitiesJson).SingleOrDefaultAsync(ct);
        if (!(JsonSerializer.Deserialize<string[]>(grants ?? "[]") ?? []).Contains(capability, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("The installation has not been granted this action control.");
    }
}
