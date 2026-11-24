using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Contracts.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Compute;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed partial class ExecutionFleetService
{
    private static readonly string[] HostedComputeFeatures = ["docker", "docker-apps-v1"];

    public async Task<OfficeComputeSetupPlan?> PrepareHostedComputeAsync(OfficeComputeSetupRequest request, CancellationToken token)
    {
        if (request.SetupReceipt is not { Length: > 0 and <= 256 } || request.MachineName is not { Length: > 0 and <= 255 } ||
            request.OperatingSystem != "windows" || request.Architecture != "x64" || request.ImageDigest is not { Length: 71 } ||
            !request.ImageDigest.StartsWith("sha256:", StringComparison.Ordinal) || request.ImageDigest.AsSpan(7).ContainsAnyExcept("0123456789abcdef") ||
            !Version.TryParse(request.OfficeVersion, out var version) || version.Build < 0 || version.ToString(3) != request.OfficeVersion)
            return null;
        var receipt = Hash(request.SetupReceipt);
        var machine = MachineBindingHash(request.MachineName, request.OperatingSystem, request.Architecture);
        var now = timeProvider.GetUtcNow();
        var sessions = dbContext.LocalOfficeSetupSessions.Where(x => x.Id == request.AssistedSetupSessionId &&
            x.SetupReceiptHash == receipt && x.MachineBindingHash == machine && x.ExpiresAt > now && x.Status == LocalOfficeSetupSessionStatus.Redeemed &&
            x.RecoveryAction == "none" && x.ExecutionNodeEnrollment != null && x.ExecutionNodeEnrollment.Status == ExecutionEnrollmentStatus.Available);
        var initial = await sessions.AsNoTracking().SingleOrDefaultAsync(token);
        if (initial is null) return null;
        if (initial.AllocatableCpuCount < 3 || initial.AllocatableMemoryMb < 2176 || initial.AllocatableDiskMb < 20544 || initial.MaximumConcurrentWorkloads < 2)
            return new(false, "Office capacity is too small to reserve a compute workspace.");
        var developmentImages = fleetOptions?.Value.AllowUnpinnedDevelopmentImages == true;
        var catalog = services?.GetService<IOptions<OfficeComputeCatalogOptions>>()?.Value ?? new();
        // Release metadata is read independently by Headquarters. The installer's digest only identifies the disk it received.
        var configuredScopes = catalog.ApprovedImages.Select(x => x.OrganizationId).ToList();
        if (catalog.CombinedInstallImage is { } installed) configuredScopes.Add(installed.OrganizationId);
        var hasLocalPolicy = configuredScopes.Count > 0 && await dbContext.CoreOrganizationUsers.AnyAsync(x =>
            x.ApplicationUserId == initial.CreatedByUserId && configuredScopes.Contains(x.OrganizationId) &&
            x.EmployeeType == EmployeeType.Human && x.PermissionLevel == OrganizationPermissionLevel.Owner && x.IsActive && x.ArchivedAt == null, token);
        var releaseMatches = developmentImages && !hasLocalPolicy && initial.ComputeReservationImageDigest is null &&
            await CurrentHostedReleaseApprovesAsync(request, token);
        await using var tx = dbContext.Database.IsRelational() ? await dbContext.Database.BeginTransactionAsync(token) : null;
        var locks = new ProjectWorkPolicy(dbContext, timeProvider);
        await locks.LockAsync(initial.Id, token);
        await FirstOfficeBusiness.LockAsync(dbContext, token);
        var session = await sessions.Include(x => x.ExecutionNodeEnrollment!).ThenInclude(e => e.ExecutionPool).SingleOrDefaultAsync(token);
        if (session?.ExecutionNodeEnrollment?.ExecutionPool is not { IsEnabled: true } pool) return null;
        if (session.ComputeReservationBusinessId is { } reserved)
        {
            if (session.ComputeReservationImageDigest != request.ImageDigest || !OfficeComputePlacement.Allows(pool, reserved)) return null;
            if (await dbContext.SystemConfigurations.AnyAsync(x => x.FirstOfficeBusinessId == reserved &&
                x.FirstOfficeSetupUserId == session.CreatedByUserId, token))
                return WorkspacePlan(session, reserved);
        }
        var owners = await dbContext.CoreOrganizationUsers.AsNoTracking().Where(x => x.ApplicationUserId == session.CreatedByUserId &&
                x.EmployeeType == EmployeeType.Human && x.PermissionLevel == OrganizationPermissionLevel.Owner && x.IsActive && x.ArchivedAt == null)
            .Select(x => x.OrganizationId).Distinct().ToListAsync(token);
        var eligible = owners.Where(id => (!session.ComputeReservationBusinessId.HasValue || session.ComputeReservationBusinessId == id) &&
            OfficeComputePlacement.Allows(pool, id)).Take(2).ToArray();
        if (eligible.Length == 0 && releaseMatches && OfficeComputePlacement.Allows(pool, null))
        {
            var future = await FirstOfficeBusiness.ReserveAsync(dbContext, session.CreatedByUserId, token);
            if (future.HasValue && OfficeComputePlacement.Allows(pool, future.Value))
            {
                session.ComputeReservationBusinessId = future;
                session.ComputeReservationImageDigest = request.ImageDigest;
                await dbContext.SaveChangesAsync(token);
                if (tx is not null) await tx.CommitAsync(token);
                return WorkspacePlan(session, future.Value);
            }
        }
        if (eligible.Length != 1) return new(false, "The Office needs one eligible business owned by the setup user.");
        var business = eligible[0];
        await locks.LockAsync(business, token);
        var matchingReplay = session.ComputeReservationBusinessId == business && session.ComputeReservationImageDigest == request.ImageDigest;
        if (session.ComputeReservationBusinessId.HasValue && !matchingReplay) return null;
        var approvals = catalog.ForBusiness(business).ToArray();
        var approved = approvals.Length > 0 ? approvals.Any(x => x.ImageDigest == request.ImageDigest && x.Features is not null &&
            HostedComputeFeatures.ToHashSet(StringComparer.Ordinal).SetEquals(x.Features)) : matchingReplay || releaseMatches;
        if (!approved) return new(false, "Headquarters could not verify this compute image against its release catalog.");
        var id = OfficeComputePlacement.TemplateId(request.ImageDigest);
        var template = new ComputeTemplate(id, "linux", "x64", request.ImageDigest, HostedComputeFeatures.ToHashSet(StringComparer.Ordinal));
        var registration = await dbContext.ComputeTemplates.SingleOrDefaultAsync(x => x.OrganizationId == business && x.TemplateId == id, token);
        if (registration is null)
            dbContext.ComputeTemplates.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, TemplateId = id, Enabled = true,
                TemplateJson = JsonSerializer.Serialize(template, ComputeProtocol.Json), UpdatedAt = now });
        else if (!registration.Enabled || JsonSerializer.Deserialize<ComputeTemplate>(registration.TemplateJson, ComputeProtocol.Json) is not { Enabled: true } existing ||
            existing.Id != id || existing.ImageDigest != request.ImageDigest || existing.OperatingSystem != "linux" ||
            existing.Architecture != "x64" || !existing.Features.SetEquals(HostedComputeFeatures))
            return new(false, "The registered compute template is disabled or differs from this release.");
        session.ComputeReservationBusinessId = business;
        session.ComputeReservationImageDigest = request.ImageDigest;
        await dbContext.SaveChangesAsync(token);
        if (tx is not null) await tx.CommitAsync(token);
        return WorkspacePlan(session, business);
    }

    public static OfficeComputeSetupPlan WorkspacePlan(LocalOfficeSetupSession session, Guid business)
    {
        // Sessions redeemed by older installers must replay the exact reservation originally issued.
        if (session.WorkspaceBudgetJson is null) return new(true, null, business, 1, 2, 2048, 20480);
        var budget = JsonSerializer.Deserialize<OfficeWorkspaceBudget>(session.WorkspaceBudgetJson)!;
        return new(true, null, business, budget.CpuCount, budget.CpuCount, budget.MemoryMiB, budget.DiskMiB);
    }

    private async Task<bool> CurrentHostedReleaseApprovesAsync(OfficeComputeSetupRequest request, CancellationToken token)
    {
        if (services?.GetService<IHttpClientFactory>() is not { } factory) return false;
        const string official = "https://github.com/CrosswiredStudios/CSweet.Office/releases/latest/download/office-bootstrap.json";
        using var client = factory.CreateClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(official, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri is not { Scheme: "https", Port: 443, UserInfo: "" } origin ||
            !(origin.Host == "github.com" || origin.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal))) return false;
        await response.Content.LoadIntoBufferAsync(1024 * 1024, timeout.Token);
        try
        {
            var metadata = JsonSerializer.Deserialize<DevelopmentRelease>(await response.Content.ReadAsByteArrayAsync(timeout.Token),
                JsonSerializerOptions.Web);
            return metadata is { Signing: "development", RequiresHostCertification: true, GuestBrokerProtocolVersion: "1.1",
                ComputeImage.Features: { Length: 2 } features } && metadata.OfficeVersion == request.OfficeVersion &&
                metadata.ComputeImage.ImageDigest == request.ImageDigest && features.ToHashSet(StringComparer.Ordinal).SetEquals(HostedComputeFeatures);
        }
        catch (JsonException) { return false; }
    }

    private sealed record DevelopmentRelease(string OfficeVersion, string Signing, bool RequiresHostCertification,
        string GuestBrokerProtocolVersion, DevelopmentImage? ComputeImage);
    private sealed record DevelopmentImage(string ImageDigest, string[] Features);
}
