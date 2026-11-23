using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Office.Contracts.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

public sealed record OfficeComputeImageApproval(Guid OrganizationId, string ImageDigest, string[] Features);
public sealed class OfficeComputeCatalogOptions
{
    public OfficeComputeImageApproval[] ApprovedImages { get; set; } = [];
    public OfficeComputeImageApproval? CombinedInstallImage { get; set; }

    public IEnumerable<OfficeComputeImageApproval> ForBusiness(Guid business)
    {
        var configured = ApprovedImages.Where(x => x.OrganizationId == business).ToArray();
        if (configured.Length != 0) return configured;
        return CombinedInstallImage is { } installed && installed.OrganizationId == business ? [installed] : [];
    }
}

public static class OfficeComputePlacement
{
    public const string ProviderId = "office-hyperv";
    private const string TemplatePrefix = "office-linux-";
    internal static readonly ComputeResources WorkspaceResources = new(2, 2048, 20480);
    private sealed record Capacity(int Slots, ComputeResources Resources);

    public static string TemplateId(string digest) => TemplatePrefix + digest.Substring(7, 48);
    public static bool IsOfficeTemplate(string templateId) =>
        templateId.Length == TemplatePrefix.Length + 48 && templateId.StartsWith(TemplatePrefix, StringComparison.Ordinal) &&
        !templateId.AsSpan(TemplatePrefix.Length).ContainsAnyExcept("0123456789abcdef");

    // Called only from the authenticated Office control stream. The business catalog is
    // independently approved in Headquarters. The machine cannot publish its own image there.
    public static async Task ApplyOfferAsync(CSweetDbContext db, ExecutionNode office, OfficeComputeOffer? offer,
        DateTimeOffset now, CancellationToken token, OfficeComputeCatalogOptions? catalog = null)
    {
        office.ComputeTemplateId = null;
        office.ComputeCapacityJson = null;
        if (offer is null || office.ApprovedAt is null || office.RevokedAt is not null || office.CertificateExpiresAt is null || office.CertificateExpiresAt <= now ||
            office.IssuedCertificateBase64 is null || !Guid.TryParseExact(offer.OrganizationId, "D", out var business) ||
            offer.ImageDigest.Length != 71 || !offer.ImageDigest.StartsWith("sha256:", StringComparison.Ordinal) ||
            offer.ImageDigest.AsSpan(7).ContainsAnyExcept("0123456789abcdef") || offer.TemplateId != TemplateId(offer.ImageDigest) ||
            offer.MaximumEnvironments < 1 || offer.CpuCount < 2 || offer.MemoryMib < 2048 || offer.DiskMib < 20480 ||
            offer.Features.Count > 64) return;
        var pool = await db.ExecutionPools.AsNoTracking().SingleAsync(x => x.Id == office.ExecutionPoolId, token);
        if (!Allows(pool, business)) return;
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(business.ToByteArray(), 0)})", token);
        var template = await db.ComputeTemplates.SingleOrDefaultAsync(x => x.OrganizationId == business &&
            x.TemplateId == offer.TemplateId, token);
        var approvals = catalog?.ForBusiness(business).ToArray() ?? [];
        if (template is null && approvals.Length == 0 && await db.LocalOfficeSetupSessions.AsNoTracking().AnyAsync(x =>
                x.ExecutionNodeId == office.Id && x.ComputeReservationBusinessId == business &&
                x.ComputeReservationImageDigest == offer.ImageDigest &&
                (x.Status == LocalOfficeSetupSessionStatus.Ready || x.Status == LocalOfficeSetupSessionStatus.Connected) &&
                db.CoreOrganizationUsers.Any(u => u.OrganizationId == business && u.ApplicationUserId == x.CreatedByUserId &&
                    u.IsActive && u.ArchivedAt == null && u.EmployeeType == CSweet.Domain.Core.EmployeeType.Human &&
                    u.PermissionLevel == CSweet.Domain.Core.OrganizationPermissionLevel.Owner), token))
            approvals = [new(business, offer.ImageDigest, ["docker", "docker-apps-v1"])];
        if (template is null && approvals.FirstOrDefault(x =>
            x.ImageDigest == offer.ImageDigest && x.Features is { Length: > 0 and <= 64 } &&
            x.Features.Contains("docker") && x.Features.All(ComputeSpecification.Identifier) &&
            x.Features.ToHashSet(StringComparer.Ordinal).SetEquals(offer.Features)) is { } approval &&
            await db.CoreOrganizations.AnyAsync(x => x.Id == business, token))
        {
            var image = new ComputeTemplate(offer.TemplateId, "linux", "x64", approval.ImageDigest,
                approval.Features.Order(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal));
            template = new() { Id = Guid.NewGuid(), TemplateId = image.Id, Enabled = true, OrganizationId = business,
                TemplateJson = JsonSerializer.Serialize(image, ComputeProtocol.Json), UpdatedAt = now };
            db.ComputeTemplates.Add(template);
        }
        if (template is not { Enabled: true } || JsonSerializer.Deserialize<ComputeTemplate>(template.TemplateJson, ComputeProtocol.Json) is not
            { OperatingSystem: "linux", Architecture: "x64", Enabled: true } definition ||
            definition.Id != offer.TemplateId || definition.ImageDigest != offer.ImageDigest ||
            !definition.Features.Contains("docker") || !definition.Features.SetEquals(offer.Features)) return;
        using var cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(office.IssuedCertificateBase64));
        using var key = cert.GetECDsaPublicKey();
        if (!cert.Thumbprint.Equals(office.CertificateThumbprint, StringComparison.OrdinalIgnoreCase) ||
            key is null || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value) return;
        var spki = key.ExportSubjectPublicKeyInfo();
        var keyId = "office-" + Convert.ToHexStringLower(SHA256.HashData(spki))[..40];
        var node = await db.ComputeNodes.SingleOrDefaultAsync(x => x.Id == office.Id, token);
        if (node is null)
        {
            node = new() { Id = office.Id, OrganizationId = business, Name = office.Name, ProviderId = ProviderId,
                VerificationPublicKeyBase64 = Convert.ToBase64String(spki), KeyId = keyId, Enabled = true, UpdatedAt = now };
            db.ComputeNodes.Add(node);
        }
        else if (node.OrganizationId != business || node.ProviderId != ProviderId || !node.Enabled ||
            node.VerificationPublicKeyBase64 != Convert.ToBase64String(spki) || node.KeyId != keyId) return;
        var placement = await db.ComputeTemplatePlacements.SingleOrDefaultAsync(x => x.TemplateRegistrationId == template.Id &&
            x.NodeId == office.Id, token);
        if (placement is null)
            db.ComputeTemplatePlacements.Add(new() { Id = Guid.NewGuid(), OrganizationId = business,
                TemplateRegistrationId = template.Id, NodeId = office.Id, Enabled = true, UpdatedAt = now });
        else if (!placement.Enabled || placement.OrganizationId != business) return;
        office.ComputeTemplateId = template.TemplateId;
        office.ComputeCapacityJson = JsonSerializer.Serialize(new Capacity(offer.MaximumEnvironments,
            new(offer.CpuCount, offer.MemoryMib, offer.DiskMib)), ComputeProtocol.Json);
    }

    public static async Task WakeWaitingAsync(CSweetDbContext db, Guid business, Guid office, long epoch,
        string template, DateTimeOffset now, CancellationToken token)
    {
        var released = await db.ComputeEnvironments.Where(x => x.OrganizationId == business && x.ProviderNodeId == office)
            .MaxAsync(x => x.TeardownConfirmedAt, token);
        var wake = $"office-compute:{office:N}:{epoch}:{template}:{released?.UtcTicks ?? 0}:";
        var waiting = await db.Set<OfficeComputeCapacityRequest>().AsNoTracking().Where(x => x.OrganizationId == business &&
            !db.ComputeEnvironments.Any(e => e.InstallationId == x.InstallationId && e.TeardownConfirmedAt == null) &&
            !db.AgentPlatformEventOutbox.Any(sent => sent.OrganizationId == business &&
                sent.TargetInstallationId == x.InstallationId && sent.IdempotencyKey.StartsWith(wake))).OrderBy(x => x.InstallationId)
            .Select(x => x.InstallationId).Take(64).ToListAsync(token);
        foreach (var installation in waiting)
        {
            var key = wake + installation.ToString("N");
            var id = new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
            db.AgentPlatformEventOutbox.Add(new() { Id = id, OrganizationId = business, TargetInstallationId = installation,
                EventType = "com.csweet.compute.available.v1", DataJson = JsonSerializer.Serialize(new { officeId = office, templateId = template }),
                IdempotencyKey = key, OccurredAt = now, NextAttemptAt = now });
        }
    }

    public static async Task<(ComputeTemplateRegistration Template, ComputeNodeRegistration Node)?> FindAsync(
        CSweetDbContext db, Guid business, DateTimeOffset now, CancellationToken token, string? templateId = null,
        Guid? officeId = null, ComputeResources? requested = null)
    {
        var earliest = now.AddSeconds(-30);
        var query = from office in db.OfficesForPlacement(now).AsNoTracking()
                    join node in db.ComputeNodes on office.Id equals node.Id
                    join place in db.ComputeTemplatePlacements on node.Id equals place.NodeId
                    join template in db.ComputeTemplates on place.TemplateRegistrationId equals template.Id
                    join pool in db.ExecutionPools on office.ExecutionPoolId equals pool.Id
                    where node.ProviderId == ProviderId && node.OrganizationId == business && node.Enabled &&
                        place.Enabled && place.OrganizationId == business && template.Enabled && template.OrganizationId == business &&
                        office.ComputeTemplateId == template.TemplateId && (templateId == null || template.TemplateId == templateId) &&
                        (officeId == null || office.Id == officeId) && office.Status == ExecutionNodeStatus.Ready &&
                        office.ApprovedAt != null && office.RevokedAt == null && office.DrainingAt == null &&
                        office.WorkPauseReason == null && office.CertificateExpiresAt > now && office.LastHeartbeatAt >= earliest && pool.IsEnabled
                    select new { template, node, pool, office.ComputeCapacityJson };
        Guid? after = null;
        do
        {
            var candidates = await query.Where(x => after == null || x.node.Id.CompareTo(after.Value) > 0)
                .OrderBy(x => x.node.Id).Take(32).ToListAsync(token);
            foreach (var row in candidates)
            {
                if (!IsOfficeTemplate(row.template.TemplateId) || !Allows(row.pool, business)) continue;
                if (officeId.HasValue || await HasRoomAsync(db, row.node.Id, row.ComputeCapacityJson, requested ?? WorkspaceResources, token))
                    return (row.template, row.node);
            }
            after = candidates.Count == 32 ? candidates[^1].node.Id : null;
        } while (after.HasValue);
        return null;
    }

    private static async Task<bool> HasRoomAsync(CSweetDbContext db, Guid node, string? json, ComputeResources requested, CancellationToken token)
    {
        if (json is null || JsonSerializer.Deserialize<Capacity>(json, ComputeProtocol.Json) is not { } capacity) return false;
        long cpu = requested.CpuCount, memory = requested.MemoryMiB, disk = requested.DiskMiB;
        var count = 1;
        var held = db.ComputeEnvironments.AsNoTracking().Where(x => x.ProviderNodeId == node && x.TeardownConfirmedAt == null);
        await foreach (var spec in held.Select(x => x.SpecificationJson).AsAsyncEnumerable().WithCancellation(token))
        {
            if (JsonSerializer.Deserialize<ComputeSpecification>(spec, ComputeProtocol.Json)?.Resources is not { } used) return false;
            count++;
            cpu += used.CpuCount;
            memory = checked(memory + used.MemoryMiB);
            disk = checked(disk + used.DiskMiB);
        }
        return count <= capacity.Slots && cpu <= capacity.Resources.CpuCount && memory <= capacity.Resources.MemoryMiB &&
            disk <= capacity.Resources.DiskMiB && requested.GpuCount == 0;
    }

    internal static bool Allows(ExecutionPool pool, Guid? business)
    {
        if (!pool.IsEnabled) return false;
        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(pool.AllowedBusinessIdsJson);
            return ids is { Length: 0 } || business is { } id && ids?.Contains(id.ToString("D"), StringComparer.Ordinal) == true;
        }
        catch (JsonException) { return false; }
    }
}
