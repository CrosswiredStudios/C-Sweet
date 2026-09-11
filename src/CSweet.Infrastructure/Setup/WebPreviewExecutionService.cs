using System.Data;
using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using CSweet.Isolation.Security;
using CSweet.WebHost.Contracts;
using CSweet.WebHost.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed partial class WebPreviewExecutionService(CSweetDbContext db, WebPreviewGrantService grants,
    WebPreviewArtifactService artifacts, WebHostReleaseCatalog releases, IWebHostAuthorizationSigner signer,
    IOptions<WebHostRegistryOptions> registry, TimeProvider clock)
{
    private async Task<PreviewOperation> StartCoreAsync(Guid organizationId, Guid installationId, PreviewRequest request, CancellationToken token)
    {
        var actor = await grants.RequireActorAsync(organizationId, installationId, WebPreviewCapabilities.Start, token);
        await grants.RequireWorkstreamAsync(organizationId, actor.Id, request.ProjectId, token);
        await grants.RequireProviderAsync(organizationId, request.ProviderInstallationId, token);
        if (request.BuildId is not { } buildId || buildId == Guid.Empty || request.Manifest is null ||
            ManifestValidator.Validate(request.Manifest).Any(x => x.Field != "artifactDigest" || request.Manifest.ArtifactDigest is not null) || request.IdempotencyKey is not { Length: > 0 and <= 200 })
            throw new ArgumentException("Start requires a successful product build, a valid manifest and a stable request key.");
        var requestDigest = WorkloadAuthorizationEnvelope.Digest(JsonSerializer.Serialize(request, PreviewJson.Options));
        var prior = await db.WebPreviewJobs.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.InstallationId == installationId && x.IdempotencyKey == request.IdempotencyKey, token);
        if (prior is not null)
        {
            if (prior.RequestDigest != requestDigest) throw new InvalidOperationException("This preview request key already has different terms.");
            return Map(prior);
        }
        // Build preparation verifies immutable source/output bytes before any signed execution authority exists.
        using var artifact = await artifacts.PrepareAsync(organizationId, request.ProjectId, request.RepositoryId,
            buildId, request.Manifest.SourceRevision, request.Manifest.Mode, token);
        if (request.Manifest.ArtifactDigest is not null && request.Manifest.ArtifactDigest != artifact.Digest)
            throw new InvalidDataException("The supplied preview digest differs from the verified build artifact.");
        if (artifact.Length > (long)request.Manifest.Resources.DiskMb * 1024 * 1024 / 2)
            throw new InvalidDataException("The verified artifact exceeds the granted input disk allowance.");
        var manifest = request.Manifest with { ArtifactDigest = artifact.Digest };
        var finalRequest = request with { Manifest = manifest };
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        // Repeat current authority inside the admission transaction, after potentially slow artifact I/O.
        actor = await grants.RequireActorAsync(organizationId, installationId, WebPreviewCapabilities.Start, token);
        await grants.RequireWorkstreamAsync(organizationId, actor.Id, request.ProjectId, token);
        await grants.RequireProviderAsync(organizationId, request.ProviderInstallationId, token);
        if (!await db.DeliveryBuilds.AsNoTracking().AnyAsync(x => x.Id == buildId && x.OrganizationId == organizationId &&
            x.WorkstreamId == request.ProjectId && x.RepositoryId == request.RepositoryId && x.Status == "Succeeded" &&
            x.SourceRevision == manifest.SourceRevision && x.Revision == artifact.BuildRevision && x.OutputsJson == artifact.OutputsJson, token) ||
            !await db.ExecutionWorkloadAssignments.AsNoTracking().AnyAsync(x => x.Id == artifact.AssignmentId &&
                x.ResultArtifactDigest == artifact.SourceArtifactDigest && x.Status == ExecutionAssignmentStatus.Completed, token))
            throw new InvalidOperationException("The verified build changed before admission.");
        var now = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds());
        var expires = now.AddSeconds(manifest.LifetimeSeconds);
        var project = await db.WebPreviewProjectAdmissions.SingleOrDefaultAsync(x => x.WorkstreamId == request.ProjectId, token);
        if (project is null)
        {
            project = new() { WorkstreamId = request.ProjectId, OrganizationId = organizationId };
            db.WebPreviewProjectAdmissions.Add(project);
        }
        else if (project.OrganizationId != organizationId) throw new UnauthorizedAccessException("The project admission scope changed.");
        project.Revision++;
        var active = await db.WebPreviewJobs.CountAsync(x => x.OrganizationId == organizationId &&
            x.WorkstreamId == request.ProjectId && x.TeardownConfirmedAt == null, token);
        var candidates = await db.WebPreviewGrants.Where(x => x.OrganizationId == organizationId && x.WorkstreamId == request.ProjectId &&
            x.InstallationId == installationId && x.ProviderInstallationId == request.ProviderInstallationId && x.Status == "Active" &&
            x.RevokedAt == null && x.ExpiresAt >= expires).OrderBy(x => x.CreatedAt).ToListAsync(token);
        var grant = candidates.FirstOrDefault(x => PreviewPolicy.Evaluate(organizationId, installationId, finalRequest,
            JsonSerializer.Deserialize<PreviewGrant>(x.PolicyJson, PreviewJson.Options)!, WebPreviewCapabilities.Start,
            active, x.ReservedCpuSeconds, now).Allowed) ?? throw new UnauthorizedAccessException("A current hosting grant with available quota is required.");
        WebHostRegistration? selectedHost = null;
        WebHostApprovedRuntime? selectedRuntime = null;
        foreach (var host in await db.WebHostRegistrations.Where(x => x.OrganizationId == organizationId &&
            x.ProviderInstallationId == request.ProviderInstallationId && x.RevokedAt == null && x.ExpiresAt >= expires).OrderBy(x => x.Id).ToListAsync(token))
        {
            var release = releases.Select(host, expires);
            if (release is null) continue;
            var heartbeat = JsonSerializer.Deserialize<WebHostHeartbeat>(host.ReportedHeartbeatJson!, PreviewJson.Options)!;
            var capacity = JsonSerializer.Deserialize<ResourceBudget>(host.MaximumCapacityJson, PreviewJson.Options)!;
            var assigned = await db.WebPreviewJobs.Where(x => x.WebHostId == host.Id && x.TeardownConfirmedAt == null).ToListAsync(token);
            var used = assigned.Select(x => JsonSerializer.Deserialize<PreviewManifest>(x.ManifestJson, PreviewJson.Options)!.Resources).ToArray();
            if (!manifest.Resources.Fits(heartbeat.Available) || used.Sum(x => (long)x.CpuCount) + manifest.Resources.CpuCount > capacity.CpuCount ||
                used.Sum(x => (long)x.MemoryMb) + manifest.Resources.MemoryMb > capacity.MemoryMb ||
                used.Sum(x => (long)x.DiskMb) + manifest.Resources.DiskMb > capacity.DiskMb) continue;
            selectedHost = host; selectedRuntime = release; break;
        }
        if (selectedHost is null || selectedRuntime is null) throw new InvalidOperationException("No certified WebHost has capacity for this preview.");
        var id = Guid.NewGuid();
        var spec = new ProductWorkloadSpecification(1, organizationId, request.ProjectId, installationId, request.ProviderInstallationId,
            grant.Id, grant.Revision, request.RepositoryId, buildId, ProductWorkloadKind.Preview, manifest, selectedRuntime.Provider.GuestImageDigest);
        var specJson = JsonSerializer.Serialize(spec, PreviewJson.Options);
        var assignment = new SignedProductAssignment(1, selectedHost.Id, Guid.NewGuid(), id, 1, selectedRuntime.Provider.Id,
            specJson, WorkloadAuthorizationEnvelope.Digest(specJson), registry.Value.AuthorizationVerificationKeyId, "", now, expires);
        assignment = assignment with { SignatureBase64 = signer.Sign(assignment.Payload()) };
        var job = new WebPreviewJobRecord
        {
            Id = id, OrganizationId = organizationId, WorkstreamId = request.ProjectId, InstallationId = installationId,
            ProviderInstallationId = request.ProviderInstallationId, GrantId = grant.Id, GrantRevision = grant.Revision,
            RepositoryId = request.RepositoryId, BuildId = buildId, WebHostId = selectedHost.Id,
            ManifestJson = JsonSerializer.Serialize(manifest, PreviewJson.Options),
            ManifestDigest = WorkloadAuthorizationEnvelope.Digest(JsonSerializer.Serialize(manifest, PreviewJson.Options)),
            AssignmentJson = JsonSerializer.Serialize(assignment, PreviewJson.Options), SourceArtifactDigest = artifact.SourceArtifactDigest,
            ArtifactLength = artifact.Length, RequestDigest = requestDigest, IdempotencyKey = request.IdempotencyKey,
            Phase = "Starting", CreatedAt = now, UpdatedAt = now, ExpiresAt = expires
        };
        grant.ReservedCpuSeconds = checked(grant.ReservedCpuSeconds + (long)manifest.Resources.CpuCount * manifest.LifetimeSeconds);
        selectedHost.Revision++;
        db.WebPreviewJobs.Add(job);
        Queue(job, "upload");
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return Map(job);
    }

    public async Task<PreviewOperation> ReadAsync(Guid organizationId, Guid installationId, Guid previewId, CancellationToken token)
    {
        var job = await RequireJobAsync(organizationId, installationId, previewId, WebPreviewCapabilities.Read, token);
        var capabilityJson = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == installationId)
            .Select(x => x.Grant!.RequiredCapabilitiesJson).SingleAsync(token);
        var capabilities = JsonSerializer.Deserialize<string[]>(capabilityJson) ?? [];
        var includeResults = capabilities.Contains(WebPreviewCapabilities.Test) || capabilities.Contains(WebPreviewCapabilities.Diagnostics);
        var tests = await db.WebHostCommands.AsNoTracking().Where(x => x.PreviewId == job.Id && x.Action == "test")
            .OrderBy(x => x.CreatedAt).Take(20).ToListAsync(token);
        var runs = tests.Select(command =>
        {
            if (command.CreatedAt <= clock.GetUtcNow().AddDays(-7)) return new PreviewTestRun(command.Id, job.Id, "Expired");
            var results = command.ResponseJson is null ? null : JsonSerializer.Deserialize<ProductRuntimeResponse>(command.ResponseJson, PreviewJson.Options)?.Guest?.TestResults;
            var status = command.Status == "Completed" ? results is null ? "Unavailable" : results.All(x => x.Passed) ? "Passed" : "Failed" : command.Status;
            return new PreviewTestRun(command.Id, job.Id, status, includeResults ? results : null);
        }).ToArray();
        return Map(job) with { TestRuns = runs };
    }

    private async Task<PreviewOperation> StopCoreAsync(Guid organizationId, Guid installationId, Guid previewId, CancellationToken token)
    {
        var job = await RequireJobAsync(organizationId, installationId, previewId, WebPreviewCapabilities.Stop, token);
        if (job.TeardownConfirmedAt is null && job.Phase != "Stopping")
        {
            job.Phase = "Stopping"; job.AccessReference = null; job.UpdatedAt = clock.GetUtcNow(); job.Revision++;
            await CancelPendingAsync(job, token);
            Queue(job, "stop");
            await db.SaveChangesAsync(token);
        }
        return Map(job);
    }

    internal async Task<WebPreviewJobRecord> RequireJobAsync(Guid organizationId, Guid installationId, Guid previewId,
        string capability, CancellationToken token)
    {
        var actor = await grants.RequireActorAsync(organizationId, installationId, capability, token);
        var job = await db.WebPreviewJobs.SingleOrDefaultAsync(x => x.Id == previewId && x.OrganizationId == organizationId &&
            x.InstallationId == installationId, token) ?? throw new UnauthorizedAccessException("The preview is outside this installation's scope.");
        await grants.RequireWorkstreamAsync(organizationId, actor.Id, job.WorkstreamId, token);
        if (capability is not (WebPreviewCapabilities.Stop or WebPreviewCapabilities.Read)) await grants.RequireProviderAsync(organizationId, job.ProviderInstallationId, token);
        return job;
    }

    internal void Queue(WebPreviewJobRecord job, string action, long after = 0)
    {
        var id = Guid.NewGuid();
        db.WebHostCommands.Add(new()
        {
            Id = id, OrganizationId = job.OrganizationId, WebHostId = job.WebHostId!.Value, PreviewId = job.Id,
            Action = action, BodyJson = JsonSerializer.Serialize(new ProductGuestRequest(id, action, DiagnosticAfterSequence: after, Renewal: action == "renew" ? JsonSerializer.Deserialize<SignedProductAssignment>(job.AssignmentJson, PreviewJson.Options) : null), PreviewJson.Options),
            CreatedAt = clock.GetUtcNow()
        });
    }
    internal async Task CancelPendingAsync(WebPreviewJobRecord job, CancellationToken token)
    {
        foreach (var command in await db.WebHostCommands.Where(x => x.PreviewId == job.Id && x.Status == "Pending").ToListAsync(token))
        { command.Status = "Cancelled"; command.Revision++; }
    }
    internal static PreviewOperation Map(WebPreviewJobRecord job) => new(job.Id, job.OrganizationId, job.WorkstreamId,
        job.InstallationId, job.ProviderInstallationId, job.GrantId, job.GrantRevision, job.ManifestDigest, job.IdempotencyKey,
        Enum.Parse<PreviewPhase>(job.Phase), job.CreatedAt, job.ExpiresAt, job.AccessReference, job.FailureCode) { Revision = job.Revision };
}



