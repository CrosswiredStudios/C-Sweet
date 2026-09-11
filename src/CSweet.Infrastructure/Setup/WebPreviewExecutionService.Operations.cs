using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Isolation.Security;
using CSweet.WebHost.Contracts;
using CSweet.WebHost.Core;
using Microsoft.EntityFrameworkCore;
namespace CSweet.Infrastructure.Setup;

public sealed partial class WebPreviewExecutionService
{
    private static Guid OperationId(Guid installation, string action, string key)
    {
        if (key is not { Length: > 0 and <= 200 }) throw new ArgumentException("A stable operation key is required.");
        return new(SHA256.HashData(Encoding.UTF8.GetBytes($"{installation:D}/{action}/{key}")).AsSpan(0, 16));
    }
    public Task<PreviewOperation> RenewAsync(Guid organization, Guid installation, RenewPreviewRequest request, CancellationToken token) => AttemptAsync(async () =>
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        var job = await RequireJobAsync(organization, installation, request.PreviewId, WebPreviewCapabilities.Renew, token);
        var id = OperationId(installation, "renew", request.IdempotencyKey);
        var prior = await db.WebHostCommands.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        if (prior is not null)
        {
            var renewal = JsonSerializer.Deserialize<ProductGuestRequest>(prior.BodyJson, PreviewJson.Options)?.Renewal;
            if (prior.PreviewId != job.Id || renewal is null || renewal.ExpiresAt != job.CreatedAt.AddSeconds(request.TotalLifetimeSeconds))
                throw new InvalidOperationException("This renewal key has different terms.");
            return Map(job);
        }
        await RequireLiveExecutionAsync(job, token);
        if (job.Phase != "Ready") throw new InvalidOperationException("Only a ready preview can be renewed.");
        var assignment = JsonSerializer.Deserialize<SignedProductAssignment>(job.AssignmentJson, PreviewJson.Options)!;
        var spec = JsonSerializer.Deserialize<ProductWorkloadSpecification>(assignment.SpecificationJson, PreviewJson.Options)!;
        var record = await db.WebPreviewGrants.SingleAsync(x => x.Id == job.GrantId, token);
        var policy = JsonSerializer.Deserialize<PreviewGrant>(record.PolicyJson, PreviewJson.Options)!;
        if (request.TotalLifetimeSeconds < 300) throw new ArgumentException("Renewal needs a total lifetime of at least five minutes.");
        var expires = job.CreatedAt.AddSeconds(request.TotalLifetimeSeconds);
        var extraCpu = checked((long)spec.Manifest.Resources.CpuCount * (request.TotalLifetimeSeconds - spec.Manifest.LifetimeSeconds));
        if (!policy.Capabilities.Contains(WebPreviewCapabilities.Renew) || request.TotalLifetimeSeconds > policy.MaximumLifetimeSeconds ||
            expires <= job.ExpiresAt || expires > record.ExpiresAt || extraCpu <= 0 || extraCpu > policy.MaximumCpuSeconds - record.ReservedCpuSeconds)
            throw new UnauthorizedAccessException("Renewal exceeds the current grant's lifetime or CPU-time allowance.");
        var host = await db.WebHostRegistrations.SingleAsync(x => x.Id == job.WebHostId, token);
        if (releases.Select(host, expires) is null) throw new UnauthorizedAccessException("A current certified host must cover the renewed lease.");
        var manifest = spec.Manifest with { LifetimeSeconds = request.TotalLifetimeSeconds };
        var json = JsonSerializer.Serialize(spec with { Manifest = manifest }, PreviewJson.Options);
        var renewed = assignment with { SpecificationJson = json, SpecificationDigest = WorkloadAuthorizationEnvelope.Digest(json),
            ExpiresAt = expires, FencingEpoch = checked(assignment.FencingEpoch + 1), SignatureBase64 = "" };
        renewed = renewed with { SignatureBase64 = signer.Sign(renewed.Payload()) };
        record.ReservedCpuSeconds = checked(record.ReservedCpuSeconds + extraCpu); host.Revision++;
        job.AssignmentJson = JsonSerializer.Serialize(renewed, PreviewJson.Options); job.ManifestJson = JsonSerializer.Serialize(manifest, PreviewJson.Options);
        job.ManifestDigest = WorkloadAuthorizationEnvelope.Digest(job.ManifestJson); job.ExpiresAt = expires; job.Phase = "Starting"; job.Revision++;
        db.WebHostCommands.Add(new() { Id = id, OrganizationId = organization, WebHostId = host.Id, PreviewId = job.Id, Action = "renew", CreatedAt = clock.GetUtcNow(),
            BodyJson = JsonSerializer.Serialize(new ProductGuestRequest(id, "renew", Renewal: renewed), PreviewJson.Options) });
        await db.SaveChangesAsync(token); if (transaction is not null) await transaction.CommitAsync(token);
        return Map(job);
    });
    public Task<PreviewTestRun> TestAsync(Guid organization, Guid installation, RunPreviewTestsRequest request, CancellationToken token) => AttemptAsync<PreviewTestRun>(async () =>
    {
        var job = await RequireJobAsync(organization, installation, request.PreviewId, WebPreviewCapabilities.Test, token);
        BrowserTestPolicy.Validate(request.Checks);
        var id = OperationId(installation, "test", request.IdempotencyKey);
        var body = JsonSerializer.Serialize(new ProductGuestRequest(id, "test", Checks: request.Checks), PreviewJson.Options);
        var command = await db.WebHostCommands.SingleOrDefaultAsync(x => x.Id == id, token);
        if (command is not null)
        {
            if (command.PreviewId == job.Id && command.CreatedAt <= clock.GetUtcNow().AddDays(-7)) return new(id, job.Id, "Expired");
            if (command.PreviewId == job.Id && command.BodyJson == "{}") return new(id, job.Id, "Unavailable");
            if (command.PreviewId != job.Id || command.BodyJson != body) throw new InvalidOperationException("This test key has different terms.");
            var results = command.ResponseJson is null ? null : JsonSerializer.Deserialize<ProductRuntimeResponse>(command.ResponseJson, PreviewJson.Options)?.Guest?.TestResults;
            return new(id, job.Id, command.Status == "Completed" ? results is null ? "Unavailable" : results.All(x => x.Passed) ? "Passed" : "Failed" : command.Status, results);
        }
        await RequireOperationGrantAsync(job, WebPreviewCapabilities.Test, token);
        if (job.Phase != "Ready" || job.ExpiresAt < clock.GetUtcNow().AddMinutes(1)) throw new InvalidOperationException("Tests require a ready preview with one minute remaining.");
        // Touch the same admission row so concurrent test requests cannot overrun the per-preview bound.
        var admission = await db.WebPreviewProjectAdmissions.SingleAsync(x => x.WorkstreamId == job.WorkstreamId, token); admission.Revision++;
        if (await db.WebHostCommands.CountAsync(x => x.PreviewId == job.Id && x.Action == "test", token) >= 20) throw new InvalidOperationException("This preview has reached its test-run limit.");
        db.WebHostCommands.Add(new() { Id = id, OrganizationId = organization, WebHostId = job.WebHostId!.Value, PreviewId = job.Id, Action = "test", BodyJson = body, CreatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(token); return new(id, job.Id, "Pending");
    });
    private async Task RequireOperationGrantAsync(WebPreviewJobRecord job, string capability, CancellationToken token)
    {
        await RequireLiveExecutionAsync(job, token);
        await RequireJobAsync(job.OrganizationId, job.InstallationId, job.Id, capability, token);
        var grant = await db.WebPreviewGrants.AsNoTracking().SingleAsync(x => x.Id == job.GrantId, token);
        if (!JsonSerializer.Deserialize<PreviewGrant>(grant.PolicyJson, PreviewJson.Options)!.Capabilities.Contains(capability))
            throw new UnauthorizedAccessException("This operation needs an explicit standing hosting grant.");
    }
    public async Task<Guid> AuthorizeBuildAsync(Guid organization, Guid installation, PreviewBuildRequest input, CancellationToken token)
    {
        await grants.RequireActorAsync(organization, installation, "platform.build.request.v2", token);
        var request = input.Preview ?? throw new ArgumentException("A scoped preview build is required.");
        var actor = await grants.RequireActorAsync(organization, installation, WebPreviewCapabilities.Build, token);
        await grants.RequireWorkstreamAsync(organization, actor.Id, request.ProjectId, token);
        await grants.RequireProviderAsync(organization, request.ProviderInstallationId, token);
        var candidates = await db.WebPreviewGrants.AsNoTracking().Where(x => x.OrganizationId == organization && x.InstallationId == installation &&
            x.WorkstreamId == request.ProjectId && x.ProviderInstallationId == request.ProviderInstallationId && x.Status == "Active" && x.RevokedAt == null && x.ExpiresAt > clock.GetUtcNow()).ToListAsync(token);
        if (!candidates.Any(record => PreviewPolicy.Evaluate(organization, installation, request,
            JsonSerializer.Deserialize<PreviewGrant>(record.PolicyJson, PreviewJson.Options)!, WebPreviewCapabilities.Build, 0, record.ReservedCpuSeconds,
            clock.GetUtcNow()).Problems.All(x => x.Field == "artifactDigest" && request.Manifest.ArtifactDigest is null)))
            throw new UnauthorizedAccessException("A current scoped build/hosting grant is required.");
        return actor.Id;
    }    private async Task<ProductRuntimeResponse> ValidateTestOutcomeAsync(WebPreviewJobRecord job, WebHostCommandRecord command, ProductRuntimeResponse response, CancellationToken token)
    {
        var checks = JsonSerializer.Deserialize<ProductGuestRequest>(command.BodyJson, PreviewJson.Options)!.Checks!;
        var results = response.Guest?.TestResults ?? checks.Select((_, i) => new PreviewBrowserCheckResult(i, false, "BrowserRunnerFailed", "The browser job did not return a result.")).ToArray();
        if (results.Count != checks.Count || results.Where((x, i) => x is null || x.Index != i || x.Code is not ("BrowserCheckPassed" or "BrowserAssertionFailed" or "BrowserRunnerFailed") ||
            x.Summary is not { Length: > 0 and <= 2048 } || x.Passed != (x.Code == "BrowserCheckPassed")).Any()) throw new InvalidDataException("Invalid browser test evidence.");
        var clean = results.Select(x => x with { Summary = DiagnosticSanitizer.Sanitize(x.Summary, []) }).ToArray();
        foreach (var failed in clean.Where(x => !x.Passed))
        {
            var manifest = JsonSerializer.Deserialize<PreviewManifest>(job.ManifestJson, PreviewJson.Options)!;
            var diagnostic = new PreviewDiagnostic(Guid.NewGuid(), job.Id, job.WorkstreamId, job.BuildId, manifest.SourceRevision, "browser", "browser",
                failed.Code, BrowserTestPolicy.FailureSummary(checks[failed.Index], failed), command.CreatedAt, false, command.Id.ToString("D"));
            var fingerprint = DiagnosticSanitizer.Fingerprint(diagnostic);
            if (!db.WebPreviewFindings.Local.Any(x => x.BuildId == job.BuildId && x.Fingerprint == fingerprint) &&
                !await db.WebPreviewFindings.AnyAsync(x => x.BuildId == job.BuildId && x.Fingerprint == fingerprint, token) &&
                await db.WebPreviewFindings.CountAsync(x => x.BuildId == job.BuildId, token) + db.WebPreviewFindings.Local.Count(x => x.BuildId == job.BuildId && db.Entry(x).State == EntityState.Added) < 128)
                db.WebPreviewFindings.Add(new() { Id = Guid.NewGuid(), OrganizationId = job.OrganizationId, ProjectId = job.WorkstreamId, PreviewId = job.Id,
                    BuildId = job.BuildId, Fingerprint = fingerprint, EvidenceJson = JsonSerializer.Serialize(diagnostic, PreviewJson.Options), CreatedAt = clock.GetUtcNow(), RetainUntil = command.CreatedAt.AddDays(7) });
        }
        return response with { Guest = new(command.Id, "test", PreviewPhase.Ready, TestResults: clean) };
    }}
