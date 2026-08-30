using CSweet.Application.Communications;
using CSweet.Application.Setup;
using CSweet.Communications.Abstractions;
using CSweet.Contracts.Communications;
using CSweet.Domain.Communications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CSweet.Infrastructure.Communications;

public sealed class CommunicationDeliveryWorker(IServiceScopeFactory scopeFactory, ILogger<CommunicationDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processed = await ProcessOneAsync(scope.ServiceProvider, logger, stoppingToken);
                if (!processed) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Communication delivery processing failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private static async Task<bool> ProcessOneAsync(
        IServiceProvider services,
        ILogger<CommunicationDeliveryWorker> logger,
        CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<CSweetDbContext>();
        var now = DateTimeOffset.UtcNow;
        var delivery = await db.CommunicationDeliveries.Where(x =>
                (x.Status == CommunicationDeliveryStatus.Pending && x.NextAttemptAt <= now) ||
                (x.Status == CommunicationDeliveryStatus.Leased && x.LeaseUntil < now))
            .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (delivery is null) return false;
        delivery.Status = CommunicationDeliveryStatus.Leased; delivery.LeaseOwner = Environment.MachineName;
        delivery.LeaseUntil = now.AddMinutes(2); delivery.Attempts++; delivery.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            var connection = delivery.ConnectionId.HasValue
                ? await db.CommunicationConnections.SingleOrDefaultAsync(x => x.Id == delivery.ConnectionId, cancellationToken)
                : null;
            if (connection is null)
            {
                // Employee desired state is retained in the employee row; connecting later queues a full reconciliation.
                delivery.Status = CommunicationDeliveryStatus.Delivered; delivery.LastError = "No external workspace is connected.";
            }
            else if (connection.PluginInstallationId is not Guid pluginInstallationId)
                throw new InvalidOperationException("The communication connection has no plugin installation.");
            else
            {
                var plugin = services.GetRequiredService<ICommunicationPluginClient>();
                if (delivery.Kind == CommunicationDeliveryKind.SendMessage)
                {
                    var envelope = JsonSerializer.Deserialize<OutboundCommunicationEnvelope>(delivery.PayloadJson)
                        ?? throw new InvalidOperationException("The outbound communication payload is invalid.");
                    var send = await plugin.SendAsync(pluginInstallationId, envelope, cancellationToken);
                    if (!send.Succeeded) throw new InvalidOperationException(send.Error?.Message ?? "Communication delivery failed.");
                    delivery.ExternalReceiptId = send.ExternalId;
                    if (delivery.ConversationMessageId.HasValue && send.ExternalId is not null)
                    {
                        db.ExternalMessageReferences.Add(new ExternalMessageReference
                        {
                            Id = Guid.NewGuid(), ConnectionId = connection.Id, ConversationMessageId = delivery.ConversationMessageId,
                            ChannelExternalId = envelope.DestinationExternalId, MessageExternalId = send.ExternalId,
                            ThreadExternalId = envelope.ThreadExternalId, IsInbound = false, CreatedAt = now
                        });
                    }
                }
                else if (delivery.Kind == CommunicationDeliveryKind.RegisterLinkCode)
                {
                    var request = JsonSerializer.Deserialize<CommunicationPluginLinkCodeRequest>(delivery.PayloadJson)
                        ?? throw new InvalidOperationException("The link-code payload is invalid.");
                    await plugin.RegisterLinkCodeAsync(pluginInstallationId, request.Code, request.ExpiresAt, cancellationToken);
                }
                else if (delivery.Kind == CommunicationDeliveryKind.AssignIdentity)
                {
                    var request = JsonSerializer.Deserialize<CommunicationPluginIdentityRequest>(delivery.PayloadJson)
                        ?? throw new InvalidOperationException("The identity payload is invalid.");
                    var assignment = await plugin.AssignMemberAsync(pluginInstallationId, request.WorkspaceExternalId,
                        request.ExternalUserId, request.MemberRoleExternalId, cancellationToken);
                    if (!assignment.Succeeded)
                        throw new InvalidOperationException(assignment.Error?.Message ?? "Identity assignment failed.");
                    delivery.ExternalReceiptId = assignment.ExternalId;
                }
                else
                {
                    var workspace = services.GetRequiredService<ICommunicationWorkspaceService>();
                    var plan = delivery.Kind == CommunicationDeliveryKind.DisconnectWorkspace
                        ? await BuildDisconnectPlanAsync(db, connection, cancellationToken)
                        : await workspace.PreviewAsync(connection.OrganizationId, connection.ProviderKey, cancellationToken)
                            ?? throw new InvalidOperationException("No provisioning plan is available.");
                    var result = await plugin.ApplyProvisioningAsync(pluginInstallationId, plan, cancellationToken);
                    if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(x => x.Message)));
                    foreach (var descriptor in result.Resources)
                    {
                        var resource = await db.ManagedExternalResources.SingleOrDefaultAsync(x => x.ConnectionId == connection.Id && x.Purpose == descriptor.Purpose, cancellationToken);
                        resource ??= new ManagedExternalResource { Id = Guid.NewGuid(), ConnectionId = connection.Id, OrganizationId = connection.OrganizationId, Purpose = descriptor.Purpose, CreatedAt = now };
                        if (db.Entry(resource).State == EntityState.Detached) db.ManagedExternalResources.Add(resource);
                        resource.Kind = Enum.Parse<ManagedResourceKind>(descriptor.Kind.ToString());
                        resource.ExternalId = descriptor.ExternalId; resource.ParentExternalId = descriptor.ParentExternalId;
                        resource.DisplayName = descriptor.Name;
                        resource.IsArchived = plan.Changes.FirstOrDefault(x => x.Purpose == descriptor.Purpose)?.Change == CommunicationChangeKind.Archive;
                        resource.UpdatedAt = now;
                        resource.OrganizationUserId = ParseEmployeeId(descriptor.Purpose);
                        resource.TeamId = ParseScopedId(descriptor.Purpose, "team");
                        resource.WorkstreamId = ParseScopedId(descriptor.Purpose, "workstream");
                    }
                    connection.Status = delivery.Kind == CommunicationDeliveryKind.DisconnectWorkspace
                        ? CommunicationConnectionStatus.Disconnected : CommunicationConnectionStatus.Connected;
                    connection.UpdatedAt = now;
                    if (delivery.Kind == CommunicationDeliveryKind.DisconnectWorkspace)
                    {
                        var links = await db.ExternalIdentityLinks.Where(x => x.ConnectionId == connection.Id && x.RevokedAt == null).ToListAsync(cancellationToken);
                        foreach (var link in links) { link.RevokedAt = now; link.ActiveDirectAgentOrganizationUserId = null; }
                    }
                }
                delivery.Status = CommunicationDeliveryStatus.Delivered; delivery.LastError = null;
            }
        }
        catch (Exception exception)
        {
            delivery.Status = delivery.Attempts >= 8 ? CommunicationDeliveryStatus.DeadLettered : CommunicationDeliveryStatus.Pending;
            delivery.NextAttemptAt = now.AddSeconds(Math.Min(900, Math.Pow(2, delivery.Attempts)) + Random.Shared.NextDouble() * 5);
            delivery.LastError = exception.Message;
        }
        delivery.LeaseOwner = null; delivery.LeaseUntil = null; delivery.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        if (delivery.Kind == CommunicationDeliveryKind.SendMessage)
        {
            try
            {
                await WriteDeliveryAuditAsync(
                    services.GetRequiredService<IAuditEventWriter>(),
                    db,
                    delivery,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                // Delivery has already committed. Audit unavailability must never cause an
                // externally delivered message to be sent a second time.
                logger.LogError(exception,
                    "Communication delivery {DeliveryId} committed but its audit event could not be recorded.",
                    delivery.Id);
            }
        }
        return true;
    }

    private static async Task WriteDeliveryAuditAsync(
        IAuditEventWriter audit,
        CSweetDbContext db,
        CommunicationDelivery delivery,
        CancellationToken cancellationToken)
    {
        var connection = delivery.ConnectionId.HasValue
            ? await db.CommunicationConnections.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == delivery.ConnectionId.Value, cancellationToken)
            : null;
        var actor = delivery.OrganizationUserId.HasValue
            ? await db.CoreOrganizationUsers.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == delivery.OrganizationUserId.Value, cancellationToken)
            : null;
        var delivered = delivery.Status == CommunicationDeliveryStatus.Delivered;
        var terminalFailure = delivery.Status == CommunicationDeliveryStatus.DeadLettered;
        var eventType = delivered
            ? "communication.external-delivery.delivered"
            : terminalFailure
                ? "communication.external-delivery.failed"
                : "communication.external-delivery.retry-scheduled";
        var providerName = connection?.ProviderKey ?? "external provider";
        await audit.AppendAsync(new AuditEventWriteRequest(
            eventType,
            "Communication",
            "Outbound",
            delivered ? "Delivered" : terminalFailure ? "Failed" : "Accepted",
            delivery.OrganizationId,
            "CommunicationDelivery",
            delivery.Id,
            delivered
                ? $"{actor?.DisplayName ?? "C-Sweet"} delivered a message through {providerName}."
                : terminalFailure
                    ? $"Message delivery through {providerName} failed permanently."
                    : $"Message delivery through {providerName} will be retried.",
            JsonSerializer.Serialize(new
            {
                delivery.ConnectionId,
                delivery.ConversationMessageId,
                provider = connection?.ProviderKey,
                connection?.WorkspaceExternalId,
                delivery.Attempts,
                status = delivery.Status.ToString()
            }),
            ExternalMessageId: delivery.ExternalReceiptId,
            CorrelationId: delivery.ConversationMessageId?.ToString("D"),
            Actor: actor is null
                ? new AuditActor("Platform", true, DisplayName: "C-Sweet platform")
                : new AuditActor(
                    actor.EmployeeType == CSweet.Domain.Core.EmployeeType.Agent ? "Agent" : "Human",
                    true,
                    actor.ApplicationUserId,
                    actor.Id,
                    actor.DisplayName,
                    actor.EmployeeType == CSweet.Domain.Core.EmployeeType.Agent ? actor.DisplayName : null,
                    actor.AgentInstallationId),
            Target: new AuditTarget("CommunicationProvider", providerName),
            ErrorCode: delivered ? null : terminalFailure ? "delivery_dead_lettered" : "delivery_retry_scheduled",
            ErrorMessage: delivered ? null : delivery.LastError),
            cancellationToken);
    }

    private static Guid? ParseEmployeeId(string purpose)
    {
        var segments = purpose.Split(':');
        return segments.Length >= 3 && segments[0] == "agent" && Guid.TryParse(segments[1], out var id) ? id : null;
    }

    private static async Task<WorkspaceProvisioningPlan> BuildDisconnectPlanAsync(CSweetDbContext db,
        CommunicationConnection connection, CancellationToken cancellationToken)
    {
        var resources = await db.ManagedExternalResources.Where(x => x.ConnectionId == connection.Id && !x.IsArchived).ToListAsync(cancellationToken);
        return new WorkspaceProvisioningPlan(connection.OrganizationId, connection.ProviderKey, connection.WorkspaceExternalId,
            resources.Select(x => new WorkspaceProvisioningChange(CommunicationChangeKind.Archive,
                Enum.Parse<CommunicationResourceKind>(x.Kind.ToString()), x.Purpose, x.DisplayName, x.ExternalId,
                "Disconnecting archives managed resources and preserves C-Sweet history.")).ToList(), DateTimeOffset.UtcNow);
    }

    private static Guid? ParseScopedId(string purpose, string scope)
    {
        var segments = purpose.Split(':');
        return segments.Length >= 3 && segments[0] == scope && Guid.TryParse(segments[1], out var id) ? id : null;
    }
}
