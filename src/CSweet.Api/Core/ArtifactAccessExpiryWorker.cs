using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Core;

public sealed class ArtifactAccessExpiryWorker(
    IServiceScopeFactory scopes, TimeProvider clock, ILogger<ArtifactAccessExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ExpireAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Document access expiry failed."); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task ExpireAsync(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditEventWriter>();
        var now = clock.GetUtcNow();
        var requests = await db.ArtifactAccessRequests.Where(x => x.Status == ArtifactAccessRequestStatus.Pending &&
            x.ExpiresAt.HasValue && x.ExpiresAt <= now).ToListAsync(token);
        foreach (var request in requests)
        {
            request.Status = ArtifactAccessRequestStatus.Expired; request.DecidedAt = now;
            if (request.RequestingInstallationId.HasValue)
            {
                var payload = new ArtifactAccessDecisionEvent(request.Id, request.ArtifactId, "Expired",
                    JsonSerializer.Deserialize<string[]>(request.ActionsJson) ?? [], [], [], now);
                db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
                {
                    Id = Guid.NewGuid(), OrganizationId = request.OrganizationId,
                    TargetInstallationId = request.RequestingInstallationId,
                    EventType = ArtifactPlatformCapabilities.AccessDecisionEvent,
                    DataJson = JsonSerializer.Serialize(payload),
                    IdempotencyKey = $"artifact-access:{request.Id:D}:Expired",
                    Status = AgentPlatformEventOutboxStatus.Pending, OccurredAt = now, NextAttemptAt = now
                });
            }
            await audit.AppendAsync(new AuditEventWriteRequest("artifact.access.expired", "DocumentAccess", "Internal",
                "Completed", request.OrganizationId, "Artifact", request.ArtifactId, "Artifact access request expired.",
                JsonSerializer.Serialize(new { requestId = request.Id, request.SubjectKind, request.SubjectId }),
                UseAmbientOrganization: false), token);
        }
        var grants = await db.ScopedActionGrants.Where(x => x.ScopeKind == GrantScopeKind.Artifact &&
            x.RevokedAt == null && x.ExpiresAt.HasValue && x.ExpiresAt <= now).ToListAsync(token);
        foreach (var grant in grants)
        {
            grant.RevokedAt = now; grant.Revision++;
            await audit.AppendAsync(new AuditEventWriteRequest("artifact.access.expired", "DocumentAccess", "Internal",
                "Completed", grant.OrganizationId, "Artifact", grant.ScopeId, "Artifact grant expired.",
                JsonSerializer.Serialize(new { grantId = grant.Id, grant.SubjectKind, grant.SubjectId, grant.Action, grant.Revision }),
                UseAmbientOrganization: false), token);
        }
        if (requests.Count > 0 || grants.Count > 0) await db.SaveChangesAsync(token);
    }
}
