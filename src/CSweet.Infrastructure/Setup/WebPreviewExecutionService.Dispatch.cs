using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.WorkManagement;
using CSweet.Isolation.Security;
using CSweet.WebHost.Contracts;
using CSweet.WebHost.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class WebPreviewExecutionService
{
    private async Task<WebHostRegistration> AuthenticateHostAsync(SignedWebHostMessage message, string action, CancellationToken token)
    {
        var host = await db.WebHostRegistrations.SingleOrDefaultAsync(x => x.Id == message.WebHostId, token)
            ?? throw new UnauthorizedAccessException("The product host is unavailable.");
        if (host.ExpiresAt <= clock.GetUtcNow() && action == "artifact") throw new UnauthorizedAccessException("The product host identity expired.");
        WebHostIdentity.Verify(message, registry.Value.ControlPlaneId, host.Id, host.IdentityPublicKeyBase64,
            host.LastSequence, clock.GetUtcNow(), action);
        // Expired/revoked identities remain cleanup-only: they can acknowledge teardown, but live authority checks deny new execution.
        host.LastSequence = message.Sequence; host.Revision++;
        return host;
    }

    internal async Task RequireLiveExecutionAsync(WebPreviewJobRecord job, CancellationToken token)
    {
        if (job.TeardownConfirmedAt is not null || job.Phase == "Stopping" || job.ExpiresAt <= clock.GetUtcNow())
            throw new UnauthorizedAccessException("The preview is no longer authorized to run.");
        var actor = await grants.RequireActorAsync(job.OrganizationId, job.InstallationId, WebPreviewCapabilities.Start, token);
        await grants.RequireWorkstreamAsync(job.OrganizationId, actor.Id, job.WorkstreamId, token);
        await grants.RequireProviderAsync(job.OrganizationId, job.ProviderInstallationId, token);
        var grant = await db.WebPreviewGrants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.GrantId &&
            x.OrganizationId == job.OrganizationId && x.InstallationId == job.InstallationId && x.WorkstreamId == job.WorkstreamId &&
            x.ProviderInstallationId == job.ProviderInstallationId && x.Status == "Active" && x.RevokedAt == null &&
            x.Revision == job.GrantRevision && x.ExpiresAt >= job.ExpiresAt, token);
        var host = await db.WebHostRegistrations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.WebHostId &&
            x.OrganizationId == job.OrganizationId && x.ProviderInstallationId == job.ProviderInstallationId &&
            x.RevokedAt == null && x.ExpiresAt >= job.ExpiresAt, token);
        if (grant is null || host is null || releases.Select(host, job.ExpiresAt, requireConnected: false) is null ||
            !await db.SourceControlRepositories.AsNoTracking().AnyAsync(x => x.Id == job.RepositoryId && x.OrganizationId == job.OrganizationId && x.ArchivedAt == null, token))
            throw new UnauthorizedAccessException("The preview grant, source, host or certified release is no longer current.");
    }

    private async Task<WebHostCommandPollReceipt> PollCoreAsync(SignedWebHostMessage message, CancellationToken token)
    {
        var host = await AuthenticateHostAsync(message, "poll", token);
        JsonSerializer.Deserialize<WebHostCommandPoll>(message.BodyJson, PreviewJson.Options);
        await ReconcileQueueAsync(host, token);
        var busy = await db.WebHostCommands.CountAsync(x => x.WebHostId == host.Id && x.Status == "Dispatched", token);
        WebHostCommandDelivery? delivery = null;
        if (busy < 2)
        {
            var pending = await db.WebHostCommands.Where(x => x.WebHostId == host.Id && x.Status == "Pending")
                .OrderByDescending(x => x.Action == "stop").ThenBy(x => x.CreatedAt).Take(32).ToListAsync(token);
            // Include commands created during reconciliation in the same atomic save.
            pending.InsertRange(0, db.WebHostCommands.Local.Where(x => x.WebHostId == host.Id && x.Status == "Pending" && !pending.Contains(x)));
            foreach (var command in pending.OrderByDescending(x => x.Action == "stop").ThenBy(x => x.CreatedAt))
            {
                if (command.Status != "Pending") continue;
                var job = await db.WebPreviewJobs.SingleAsync(x => x.Id == command.PreviewId && x.OrganizationId == host.OrganizationId, token);
                if (command.Action != "stop" && await db.WebHostCommands.AnyAsync(x => x.PreviewId == job.Id && x.Status == "Dispatched", token)) continue;
                if (command.Action is not ("stop" or "reconcile" or "evidence" or "diagnostics"))
                {
                    try
                    {
                        await RequireLiveExecutionAsync(job, token);
                        if (command.Action is "renew" or "test") await RequireOperationGrantAsync(job, command.Action == "renew" ? WebPreviewCapabilities.Renew : WebPreviewCapabilities.Test, token);
                        if (command.Action == "http")
                        {
                            if (command.BrowserSessionId is not { } sessionId || command.CreatedAt < clock.GetUtcNow().AddSeconds(-40))
                                throw new UnauthorizedAccessException("The browser request expired.");
                            await RequireBrowserSessionAsync(sessionId, job.Id, token);
                        }
                    }
                    catch (UnauthorizedAccessException) { command.Status = "Cancelled"; command.Revision++; continue; }
                }
                else if ((host.RevokedAt is not null || host.ExpiresAt <= clock.GetUtcNow()) && command.Action is not ("stop" or "reconcile" or "evidence"))
                { command.Status = "Cancelled"; command.Revision++; continue; }
                var assignment = JsonSerializer.Deserialize<SignedProductAssignment>(job.AssignmentJson, PreviewJson.Options)!;
                ProductRuntimeRequest runtimeRequest;
                if (command.Action is "upload" or "start")
                    runtimeRequest = new(Assignment: assignment, ArtifactLength: command.Action == "upload" ? job.ArtifactLength : null);
                else
                {
                    var now = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds());
                    var control = new SignedProductControl(1, host.Id, command.Id, job.Id, command.Action, command.BodyJson,
                        WorkloadAuthorizationEnvelope.Digest(command.BodyJson), registry.Value.AuthorizationVerificationKeyId, "", now, now.AddSeconds(60));
                    runtimeRequest = new(Control: control with { SignatureBase64 = signer.Sign(control.Payload()) });
                }
                command.Status = "Dispatched"; command.DeliveredAt = clock.GetUtcNow(); command.Revision++;
                var manifest = JsonSerializer.Deserialize<PreviewManifest>(job.ManifestJson, PreviewJson.Options)!;
                delivery = new(command.Id, job.Id, JsonSerializer.Serialize(runtimeRequest, PreviewJson.Options),
                    command.Action == "upload" ? manifest.ArtifactDigest : null, command.Action == "upload" ? job.ArtifactLength : null);
                break;
            }
        }
        await db.SaveChangesAsync(token);
        return new(message.RequestId, message.Sequence, delivery);
    }

    private async Task ReconcileQueueAsync(WebHostRegistration host, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var jobs = await db.WebPreviewJobs.Where(x => x.WebHostId == host.Id && x.TeardownConfirmedAt == null).Take(4096).ToListAsync(token);
        foreach (var job in jobs)
        {
            var commands = await db.WebHostCommands.Where(x => x.PreviewId == job.Id && (x.Status == "Pending" || x.Status == "Dispatched")).ToListAsync(token);
            foreach (var stale in commands.Where(x => x.Status == "Dispatched" && x.DeliveredAt < now.AddMinutes(-15)))
            {
                stale.Status = "Unknown"; stale.Revision++;
                // No mutation is replayed after uncertainty. First ask the protected runtime for its state.
                if (!commands.Any(x => x.Action == "reconcile" && x.Status == "Pending")) Queue(job, "reconcile");
            }
            var wasStopping = job.Phase == "Stopping";
            var authorized = true;
            try { await RequireLiveExecutionAsync(job, token); }
            catch (UnauthorizedAccessException) { authorized = false; }
            if (!authorized)
            {
                job.Phase = "Stopping"; job.AccessReference = null; job.Revision++; job.UpdatedAt = now;
                job.FailureCode ??= job.ExpiresAt <= now ? "LeaseExpired" : wasStopping ? null : "AuthorityUnavailable";
                foreach (var command in commands.Where(x => x.Status == "Pending" && x.Action != "stop"))
                { command.Status = "Cancelled"; command.Revision++; }
                if (!commands.Any(x => x.Action == "stop" && x.Status is "Pending" or "Dispatched")) Queue(job, "stop");
            }
            else if (job.Phase == "Ready" && !commands.Any(x => x.Status is "Pending" or "Dispatched") && job.UpdatedAt <= now.AddSeconds(-30))
            { Queue(job, "evidence", job.LastEvidenceSequence); job.UpdatedAt = now; job.Revision++; }
        }
    }

    private async Task<PreparedWebPreviewArtifact> ArtifactCoreAsync(SignedWebHostMessage message, CancellationToken token)
    {
        var host = await AuthenticateHostAsync(message, "artifact", token);
        var request = JsonSerializer.Deserialize<WebHostArtifactRequest>(message.BodyJson, PreviewJson.Options)
            ?? throw new ArgumentException("An exact artifact command is required.");
        var command = await db.WebHostCommands.SingleOrDefaultAsync(x => x.Id == request.CommandId && x.WebHostId == host.Id &&
            x.Action == "upload" && x.Status == "Dispatched", token) ?? throw new UnauthorizedAccessException("The artifact command is unavailable.");
        var job = await db.WebPreviewJobs.SingleAsync(x => x.Id == command.PreviewId && x.OrganizationId == host.OrganizationId, token);
        await RequireLiveExecutionAsync(job, token);
        await db.SaveChangesAsync(token); // Consume the signed request before potentially slow archive reads.
        var manifest = JsonSerializer.Deserialize<PreviewManifest>(job.ManifestJson, PreviewJson.Options)!;
        var artifact = await artifacts.PrepareAsync(job.OrganizationId, job.WorkstreamId, job.RepositoryId,
            job.BuildId, manifest.SourceRevision, manifest.Mode, token);
        try
        {
            if (artifact.Digest != manifest.ArtifactDigest || artifact.SourceArtifactDigest != job.SourceArtifactDigest || artifact.Length != job.ArtifactLength)
                throw new InvalidDataException("The dispatched artifact no longer matches its verified provenance.");
            await RequireLiveExecutionAsync(job, token);
            return artifact;
        }
        catch { artifact.Dispose(); throw; }
    }

    private async Task<WebHostCommandResultReceipt> CompleteCoreAsync(SignedWebHostMessage message, CancellationToken token)
    {
        var host = await AuthenticateHostAsync(message, "result", token);
        var result = JsonSerializer.Deserialize<WebHostCommandResult>(message.BodyJson, PreviewJson.Options)
            ?? throw new ArgumentException("A command result is required.");
        if (result.RuntimeResponseJson is not { Length: > 0 and <= 8 * 1024 * 1024 }) throw new ArgumentException("The command result exceeds its bound.");
        var command = await db.WebHostCommands.SingleOrDefaultAsync(x => x.Id == result.CommandId && x.WebHostId == host.Id, token)
            ?? throw new UnauthorizedAccessException("The command does not belong to this host.");
        var digest = WorkloadAuthorizationEnvelope.Digest(result.RuntimeResponseJson);
        if (command.Status == "Completed")
        {
            if (command.ResponseDigest != digest) throw new UnauthorizedAccessException("A completed command cannot change its result.");
        }
        else
        {
            if (command.Status is not ("Dispatched" or "Unknown")) throw new UnauthorizedAccessException("The command was not delivered.");
            var job = await db.WebPreviewJobs.SingleAsync(x => x.Id == command.PreviewId && x.OrganizationId == host.OrganizationId, token);
            var response = JsonSerializer.Deserialize<ProductRuntimeResponse>(result.RuntimeResponseJson, PreviewJson.Options)
                ?? throw new ArgumentException("The runtime response is missing.");
            var assignment = JsonSerializer.Deserialize<SignedProductAssignment>(job.AssignmentJson, PreviewJson.Options)!;
            if (response.Handle is { } handle && (handle.AssignmentId != assignment.AssignmentId || handle.WorkloadId != job.Id ||
                !Guid.TryParseExact(handle.ProviderInstanceId, "N", out _))) throw new InvalidDataException("The result belongs to another workload.");
            if (response.Guest is { } guest && (guest.RequestId != command.Id || guest.Kind != command.Action || !Enum.IsDefined(guest.Phase) || guest.Diagnostics is not null))
                throw new InvalidDataException("The guest result is not bound to the dispatched command.");
            if (command.Action == "test" && command.CreatedAt > clock.GetUtcNow().AddDays(-7)) response = await ValidateTestOutcomeAsync(job, command, response, token);
            command.Status = "Completed";
            command.ResponseJson = (command.Action == "test" && command.CreatedAt > clock.GetUtcNow().AddDays(-7) || command.Action == "http" && command.BodyJson != "{}" && command.CreatedAt >= clock.GetUtcNow().AddSeconds(-40)) ? JsonSerializer.Serialize(response, PreviewJson.Options) : null;
            command.ResponseDigest = digest;
            command.CompletedAt = clock.GetUtcNow(); command.Revision++;
            await ApplyResultAsync(job, command, response, token);
        }
        await db.SaveChangesAsync(token);
        return new(message.RequestId, message.Sequence, command.Id, true);
    }

    private async Task<WebHostCommandResultReceipt> RejectDeliveredResultAsync(SignedWebHostMessage message, CancellationToken token)
    {
        var host = await AuthenticateHostAsync(message, "result", token);
        var result = JsonSerializer.Deserialize<WebHostCommandResult>(message.BodyJson, PreviewJson.Options)
            ?? throw new InvalidDataException("A result identity is required.");
        if (result.RuntimeResponseJson is not { Length: > 0 and <= 8 * 1024 * 1024 }) throw new InvalidDataException("The result exceeds its limit.");
        var command = await db.WebHostCommands.SingleOrDefaultAsync(x => x.Id == result.CommandId && x.WebHostId == host.Id &&
            (x.Status == "Dispatched" || x.Status == "Unknown"), token) ?? throw new UnauthorizedAccessException("Only this host's delivered command can be rejected.");
        command.Status = "Completed"; command.ResponseDigest = WorkloadAuthorizationEnvelope.Digest(result.RuntimeResponseJson);
        command.ResponseJson = null; command.BodyJson = "{}"; command.CompletedAt = clock.GetUtcNow(); command.Revision++;
        var job = await db.WebPreviewJobs.SingleAsync(x => x.Id == command.PreviewId && x.OrganizationId == host.OrganizationId, token);
        if (job.TeardownConfirmedAt is null)
        {
            job.Phase = "Stopping"; job.FailureCode = "InvalidRuntimeResult"; job.AccessReference = null; job.Revision++;
            await CancelPendingAsync(job, token); Queue(job, "stop");
        }
        await db.SaveChangesAsync(token);
        return new(message.RequestId, message.Sequence, command.Id, true);
    }
    private async Task ApplyResultAsync(WebPreviewJobRecord job, WebHostCommandRecord command, ProductRuntimeResponse response, CancellationToken token)
    {
        job.UpdatedAt = clock.GetUtcNow(); job.Revision++;
        if (command.Action == "evidence" && response.Code == "Evidence" && response.Evidence is { } page)
        {
            await IngestEvidenceAsync(job, command, page, token);
            if (page.HasMore) Queue(job, "evidence", job.LastEvidenceSequence);
            return;
        }
        if (command.Action == "test") { Queue(job, "evidence", job.LastEvidenceSequence); return; }
        if (command.Action == "http" || job.TeardownConfirmedAt is not null) return;
        if (command.Action == "stop" && response.Code == "Stopped" || command.Action == "reconcile" && response.Code == "Destroyed")
        {
            job.TeardownConfirmedAt = clock.GetUtcNow(); job.AccessReference = null;
            job.Phase = job.ExpiresAt <= clock.GetUtcNow() ? "Expired" : job.FailureCode is null ? "Stopped" : "Failed";
            await CancelPendingAsync(job, token); Queue(job, "evidence", job.LastEvidenceSequence);
            return;
        }
        if (job.Phase == "Stopping") return; // A delayed initialization/result never resurrects revoked access.
        try { await RequireLiveExecutionAsync(job, token); }
        catch (UnauthorizedAccessException)
        { job.Phase = "Stopping"; job.AccessReference = null; Queue(job, "stop"); return; }
        switch (command.Action)
        {
            case "upload" when response.Code == "ArtifactReady": Queue(job, "start"); return;
            case "start" when response.Code is "Booting" or "Reconciled" && response.Handle is not null: Queue(job, "initialize"); return;
            case "renew" when response.Guest?.Phase == PreviewPhase.Ready:
            case "initialize" when response.Guest?.Phase == PreviewPhase.Ready:
                job.Phase = "Ready"; job.AccessReference = $"/api/core/organizations/{job.OrganizationId:D}/web-previews/{job.Id:D}/open"; Queue(job, "evidence", job.LastEvidenceSequence); return;
            case "reconcile" when response.Code is "Ready" or "Booting" or "Requested":
                Queue(job, response.Code == "Ready" && response.LeaseExpiresAt is { } lease && lease < job.ExpiresAt ? "renew" : "initialize"); return;
            case "diagnostics": Queue(job, "evidence", job.LastEvidenceSequence); return;
            case "evidence": return;
        }
        // A failed or uncertain execution keeps its quota until a protected stop is confirmed.
        job.Phase = "Stopping"; job.FailureCode = "RuntimeOperationFailed"; job.AccessReference = null;
        Queue(job, "stop");
    }
}
