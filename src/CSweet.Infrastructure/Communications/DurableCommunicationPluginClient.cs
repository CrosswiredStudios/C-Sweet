using System.Text.Json;
using CSweet.Communications.Abstractions;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Communications;

public sealed class DurableCommunicationPluginClient(
    CSweetDbContext db,
    AgentWorkInbox inbox) : ICommunicationPluginClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<CommunicationResult> SendAsync(
        Guid pluginInstallationId,
        OutboundCommunicationEnvelope envelope,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<OutboundCommunicationEnvelope, CommunicationResult>(
            pluginInstallationId,
            CommunicationPluginCapabilities.SendMessage,
            envelope,
            cancellationToken);

    public Task<WorkspaceProvisioningResult> ApplyProvisioningAsync(
        Guid pluginInstallationId,
        WorkspaceProvisioningPlan plan,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<WorkspaceProvisioningPlan, WorkspaceProvisioningResult>(
            pluginInstallationId,
            CommunicationPluginCapabilities.ApplyWorkspace,
            plan,
            cancellationToken);

    public async Task RegisterLinkCodeAsync(
        Guid pluginInstallationId,
        string code,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<CommunicationPluginLinkCodeRequest, CommunicationResult>(
            pluginInstallationId,
            CommunicationPluginCapabilities.RegisterLinkCode,
            new(code, expiresAt),
            cancellationToken);

    public Task<CommunicationResult> AssignMemberAsync(
        Guid pluginInstallationId,
        string workspaceExternalId,
        string externalUserId,
        string memberRoleExternalId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<CommunicationPluginIdentityRequest, CommunicationResult>(
            pluginInstallationId,
            CommunicationPluginCapabilities.AssignIdentity,
            new(workspaceExternalId, externalUserId, memberRoleExternalId),
            cancellationToken);

    private async Task<TResponse> InvokeAsync<TRequest, TResponse>(
        Guid installationId,
        string capability,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        var installation = await db.AgentInstallations.AsNoTracking()
            .SingleAsync(x => x.Id == installationId && x.IsEnabled, cancellationToken);
        var idempotencyKey = ExtractIdempotencyKey(payload)
            ?? $"{capability}:{Guid.NewGuid():N}";
        var work = await inbox.EnqueueAsync(
            installation.BusinessId,
            installation.Id,
            AgentWorkKind.Capability,
            capability,
            JsonSerializer.SerializeToElement(payload, JsonOptions),
            idempotencyKey,
            DateTimeOffset.UtcNow.AddMinutes(2),
            sourceType: "communication-platform",
            maximumAttempts: 3,
            cancellationToken: cancellationToken);
        return await inbox.WaitForResultAsync<TResponse>(
            work.Id,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);
    }

    private static string? ExtractIdempotencyKey<TRequest>(TRequest payload)
    {
        var property = typeof(TRequest).GetProperty("IdempotencyKey");
        return property?.GetValue(payload) as string;
    }
}
