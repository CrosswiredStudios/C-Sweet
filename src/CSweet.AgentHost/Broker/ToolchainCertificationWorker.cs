using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

/// <summary>
/// Completes toolchain certifications only after every fixture traverses the production build path
/// twice and produces the same normalized output manifest. Eligibility is a derived, revocable fact.
/// </summary>
public sealed class ToolchainCertificationWorker(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<ToolchainCertificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        do
        {
            try { await ReviewAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Toolchain certification review failed."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task ReviewAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var now = clock.GetUtcNow();
        var expired = await db.ToolchainCertificationRuns.Where(x => x.Status == W.ToolchainCertificationStatuses.Certified &&
            x.ExpiresAt.HasValue && x.ExpiresAt <= now).ToListAsync(token);
        foreach (var run in expired)
        {
            run.Status = W.ToolchainCertificationStatuses.Expired; run.Revision++;
            var items = await db.ToolchainInstallationEligibilities.Where(x => x.CertificationRunId == run.Id && x.RevokedAt == null).ToListAsync(token);
            foreach (var item in items) { item.RevokedAt = now; item.RevocationReason = "Certification expired."; }
        }
        var certified = await db.ToolchainCertificationRuns.Where(x => x.Status == W.ToolchainCertificationStatuses.Certified &&
            (!x.ExpiresAt.HasValue || x.ExpiresAt > now)).ToListAsync(token);
        foreach (var run in certified)
        {
            var packageDigest = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == run.ProviderInstallationId &&
                    x.IsEnabled && x.RevisionStatus == CSweet.Domain.Setup.PluginRevisionStatus.Active)
                .Select(x => x.PackageVersion!.PackageDigest).SingleOrDefaultAsync(token);
            var definitionDigest = await db.ToolchainAdapterDefinitions.AsNoTracking()
                .Where(x => x.Id == run.ToolchainDefinitionId).Select(x => x.DefinitionDigest).SingleOrDefaultAsync(token);
            if (string.Equals(NormalizeDigest(packageDigest), run.ProviderPackageDigest, StringComparison.Ordinal) &&
                string.Equals(definitionDigest, run.DefinitionDigest, StringComparison.Ordinal)) continue;
            run.Status = W.ToolchainCertificationStatuses.Revoked;
            run.RevocationReason = "The certified provider package or adapter definition changed.";
            run.Revision++;
            var items = await db.ToolchainInstallationEligibilities.Where(x => x.CertificationRunId == run.Id && x.RevokedAt == null).ToListAsync(token);
            foreach (var item in items) { item.RevokedAt = now; item.RevocationReason = run.RevocationReason; }
        }
        var running = await db.ToolchainCertificationRuns.Where(x =>
            x.Status == W.ToolchainCertificationStatuses.Pending || x.Status == W.ToolchainCertificationStatuses.Running).ToListAsync(token);
        foreach (var run in running)
        {
            var builds = await db.DeliveryBuilds.AsNoTracking().Where(x => x.CertificationRunId == run.Id).ToListAsync(token);
            if (builds.Count == 0) { Fail(run, now, "No certification builds were scheduled.", []); continue; }
            if (builds.Any(x => x.Status is W.DeliveryBuildStatuses.Queued or W.DeliveryBuildStatuses.Claimed or
                    W.DeliveryBuildStatuses.Running or W.DeliveryBuildStatuses.CancelRequested)) continue;
            var failed = builds.Where(x => x.Status != W.DeliveryBuildStatuses.Succeeded).ToList();
            if (failed.Count > 0)
            {
                Fail(run, now, $"{failed.Count} certification build(s) failed.", failed.Select(x =>
                    Check($"build:{x.RecipeKey}:{x.TargetKey}:{x.CertificationFixtureKey}:pass-{x.CertificationPass}",
                        "Failed", x.FailureSummary ?? x.Status)).ToList());
                continue;
            }
            var groups = builds.GroupBy(x => new { x.RecipeKey, x.TargetKey, x.CertificationFixtureKey }).ToList();
            var checks = new List<W.CertificationCheckResult>();
            var reproducible = true;
            foreach (var group in groups)
            {
                var first = group.SingleOrDefault(x => x.CertificationPass == 1);
                var second = group.SingleOrDefault(x => x.CertificationPass == 2);
                var firstHash = ManifestHash(first); var secondHash = ManifestHash(second);
                var matches = first is not null && second is not null && firstHash is not null && firstHash == secondHash;
                reproducible &= matches;
                checks.Add(Check($"reproducibility:{group.Key.RecipeKey}:{group.Key.TargetKey}:{group.Key.CertificationFixtureKey}",
                    matches ? "Passed" : "Failed", matches ? "Two clean runs produced matching normalized output manifests."
                        : "The two clean-run manifests are missing or do not match."));
                if (matches)
                {
                    checks.Add(Check($"lifecycle:{group.Key.RecipeKey}:{group.Key.TargetKey}:{group.Key.CertificationFixtureKey}",
                        "Passed", "Restore, build, automated tests, smoke execution, capture, package, provenance, and report completed through the leased production path."));
                }
            }
            var passOne = Aggregate(builds.Where(x => x.CertificationPass == 1).Select(ManifestHash));
            var passTwo = Aggregate(builds.Where(x => x.CertificationPass == 2).Select(ManifestHash));
            run.FirstManifestHash = passOne; run.SecondManifestHash = passTwo;
            if (!reproducible || passOne is null || passTwo is null || passOne != passTwo)
            {
                Fail(run, now, "Clean-run reproducibility failed.", checks); continue;
            }
            var installation = await db.AgentInstallations.Include(x => x.PackageVersion).AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == run.ProviderInstallationId, token);
            var definition = await db.ToolchainAdapterDefinitions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == run.ToolchainDefinitionId, token);
            var packageStillMatches = installation is { IsEnabled: true, RevisionStatus: CSweet.Domain.Setup.PluginRevisionStatus.Active } &&
                installation.PackageVersion is not null && definition is not null &&
                installation.PackageVersion.AgentId == definition.ProviderPackageId && installation.PackageVersion.Version == definition.ProviderPackageVersion &&
                string.Equals(NormalizeDigest(installation.PackageVersion.PackageDigest), run.ProviderPackageDigest, StringComparison.Ordinal) &&
                string.Equals(definition.DefinitionDigest, run.DefinitionDigest, StringComparison.Ordinal);
            if (!packageStillMatches) { Fail(run, now, "The provider package changed or was disabled during certification.", checks); continue; }
            run.Status = W.ToolchainCertificationStatuses.Certified; run.ChecksJson = JsonSerializer.Serialize(checks);
            run.CompletedAt = now; run.Revision++;
            var eligibility = await db.ToolchainInstallationEligibilities.SingleOrDefaultAsync(x =>
                x.OrganizationId == run.OrganizationId && x.ToolchainDefinitionId == run.ToolchainDefinitionId &&
                x.ProviderInstallationId == run.ProviderInstallationId, token);
            if (eligibility is null)
            {
                eligibility = new ToolchainInstallationEligibilityRecord { Id = Guid.NewGuid(), OrganizationId = run.OrganizationId,
                    ToolchainDefinitionId = run.ToolchainDefinitionId, ProviderInstallationId = run.ProviderInstallationId };
                db.ToolchainInstallationEligibilities.Add(eligibility);
            }
            eligibility.CertificationRunId = run.Id; eligibility.EnvironmentProfileKey = run.EnvironmentProfileKey;
            eligibility.EnvironmentImageDigest = run.EnvironmentImageDigest; eligibility.CertifiedAt = now;
            eligibility.ExpiresAt = run.ExpiresAt ?? now.AddDays(90); eligibility.RevokedAt = null; eligibility.RevocationReason = null;
        }
        if (expired.Count > 0 || certified.Count > 0 || running.Count > 0) await db.SaveChangesAsync(token);
    }

    private static void Fail(ToolchainCertificationRunRecord run, DateTimeOffset now, string summary,
        IReadOnlyList<W.CertificationCheckResult> checks)
    {
        run.Status = W.ToolchainCertificationStatuses.Failed; run.CompletedAt = now;
        run.ChecksJson = JsonSerializer.Serialize(checks.Count == 0 ? [Check("harness", "Failed", summary)] : checks);
        run.RevocationReason = summary; run.Revision++;
    }

    private static W.CertificationCheckResult Check(string key, string status, string summary) => new(key, status, summary, []);
    private static string? ManifestHash(DeliveryBuildRecord? build)
    {
        if (build is null || string.IsNullOrWhiteSpace(build.ProvenanceJson)) return null;
        try { return JsonSerializer.Deserialize<W.BuildExecutionProvenance>(build.ProvenanceJson)?.NormalizedOutputManifestHash; }
        catch (JsonException) { return null; }
    }
    private static string? Aggregate(IEnumerable<string?> hashes)
    {
        var values = hashes.Where(x => !string.IsNullOrWhiteSpace(x)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return values.Count == 0 ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values)))).ToLowerInvariant();
    }
    private static string? NormalizeDigest(string? value) => string.IsNullOrWhiteSpace(value)
        ? null : value.StartsWith("sha256:", StringComparison.Ordinal) ? value : $"sha256:{value}";
}
