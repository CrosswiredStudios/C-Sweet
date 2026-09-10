using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Isolation.Security;
using CSweet.WebHost.Contracts;
using CSweet.WebHost.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace CSweet.Infrastructure.Setup;

public sealed class WebHostRegistryOptions
{
    public const string SectionName = "CSweet:WebHost";
    public Guid ControlPlaneId { get; set; }
    public string AuthorizationVerificationKeyId { get; set; } = "";
    public string AuthorizationVerificationPublicKeyBase64 { get; set; } = "";
}

public sealed class WebHostRegistryService(CSweetDbContext db, WebPreviewGrantService grants,
    IOptions<WebHostRegistryOptions> options, TimeProvider clock)
{
    public async Task<WebHostBootstrap> RegisterAsync(Guid organizationId, Guid applicationUserId,
        RegisterWebHost request, CancellationToken token)
    {
        var owner = await RequireOwnerAsync(organizationId, applicationUserId, token);
        await grants.RequireProviderAsync(organizationId, request.ProviderInstallationId, token);
        var now = clock.GetUtcNow();
        if (request.RequestId == Guid.Empty || request.DisplayName is not { Length: > 0 and <= 120 } ||
            request.DisplayName.Any(char.IsControl) || string.IsNullOrWhiteSpace(request.DisplayName) ||
            request.MaximumCapacity is null || request.ExpiresAt <= now || request.ExpiresAt > now.AddDays(90))
            throw new ArgumentException("Supply a host name, bounded capacity, identity key and expiry within 90 days.");
        request.MaximumCapacity.Validate();
        WebHostIdentity.ValidatePublicKey(request.IdentityPublicKeyBase64);
        var config = options.Value;
        if (config.ControlPlaneId == Guid.Empty || config.AuthorizationVerificationKeyId is not { Length: > 0 and <= 128 })
            throw new InvalidOperationException("WebHost authorization verification has not been configured.");
        WebHostIdentity.ValidatePublicKey(config.AuthorizationVerificationPublicKeyBase64);
        var digest = WorkloadAuthorizationEnvelope.Digest(JsonSerializer.Serialize(request, PreviewJson.Options));
        var prior = await db.WebHostRegistrations.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.RegistrationRequestId == request.RequestId, token);
        if (prior is not null)
        {
            if (prior.RegistrationDigest != digest || prior.RevokedAt is not null || prior.ExpiresAt <= now)
                throw new InvalidOperationException("This host enrollment request was consumed with different or inactive terms.");
            return JsonSerializer.Deserialize<WebHostBootstrap>(prior.BootstrapJson, PreviewJson.Options)!;
        }
        var keyDigest = WorkloadAuthorizationEnvelope.Digest(request.IdentityPublicKeyBase64);
        if (await db.WebHostRegistrations.AnyAsync(x => x.IdentityKeyDigest == keyDigest, token))
            throw new InvalidOperationException("Each WebHost enrollment requires a fresh identity key.");
        var id = Guid.NewGuid();
        var bootstrap = new WebHostBootstrap(config.ControlPlaneId, organizationId,
            new(id, request.DisplayName, config.AuthorizationVerificationKeyId,
                config.AuthorizationVerificationPublicKeyBase64, now), request.ExpiresAt);
        db.WebHostRegistrations.Add(new()
        {
            Id = id, OrganizationId = organizationId, ProviderInstallationId = request.ProviderInstallationId,
            RegistrationRequestId = request.RequestId, RegisteredByOrganizationUserId = owner.Id,
            RegistrationDigest = digest, DisplayName = request.DisplayName,
            IdentityPublicKeyBase64 = request.IdentityPublicKeyBase64, IdentityKeyDigest = keyDigest,
            MaximumCapacityJson = JsonSerializer.Serialize(request.MaximumCapacity, PreviewJson.Options),
            BootstrapJson = JsonSerializer.Serialize(bootstrap, PreviewJson.Options),
            RegisteredAt = now, ExpiresAt = request.ExpiresAt
        });
        await db.SaveChangesAsync(token);
        return bootstrap;
    }

    public async Task<WebHostHeartbeatReceipt> HeartbeatAsync(SignedWebHostMessage message, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var host = await db.WebHostRegistrations.SingleOrDefaultAsync(x => x.Id == message.WebHostId, token);
        if (host is null || host.RevokedAt is not null || host.ExpiresAt <= now)
            throw new UnauthorizedAccessException("The WebHost identity is unavailable.");
        WebHostIdentity.Verify(message, options.Value.ControlPlaneId, host.Id,
            host.IdentityPublicKeyBase64, host.LastSequence, now);
        await grants.RequireProviderAsync(host.OrganizationId, host.ProviderInstallationId, token);
        var heartbeat = JsonSerializer.Deserialize<WebHostHeartbeat>(message.BodyJson, PreviewJson.Options)
            ?? throw new ArgumentException("A bounded heartbeat is required.");
        var capacity = JsonSerializer.Deserialize<ResourceBudget>(host.MaximumCapacityJson, PreviewJson.Options)!;
        if (heartbeat.WebHostId != host.Id || heartbeat.OccurredAt < message.IssuedAt.AddMinutes(-2) ||
            heartbeat.OccurredAt > now.AddSeconds(5) || heartbeat.Available is null ||
            heartbeat.Available.CpuCount < 0 || heartbeat.Available.MemoryMb < 0 || heartbeat.Available.DiskMb < 0 ||
            heartbeat.Available.MaximumProcesses < 0 || heartbeat.Available.MaximumLogBytes < 0 ||
            !heartbeat.Available.Fits(capacity) || heartbeat.Providers is not { Count: > 0 and <= 16 } ||
            heartbeat.Providers.Any(x => x is null || x.Id is not { Length: > 0 and <= 128 } ||
                x.Version is not { Length: > 0 and <= 64 } || !WorkloadAuthorizationEnvelope.IsDigest(x.GuestImageDigest) ||
                x.UnavailableReason is { Length: > 512 }) ||
            heartbeat.Providers.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != heartbeat.Providers.Count)
            throw new ArgumentException("The WebHost heartbeat does not match its registered scope and capacity.");
        host.LastSequence = message.Sequence;
        host.LastHeartbeatAt = now;
        host.ReportedHeartbeatJson = message.BodyJson;
        host.Status = "Connected";
        host.Revision++;
        // The concurrency token makes replay consumption atomic across Headquarters replicas.
        await db.SaveChangesAsync(token);
        return new(message.RequestId, message.Sequence, now, false, "CertifiedDispatchNotConfigured");
    }

    public async Task<IReadOnlyList<WebHostRegistrationView>> ListAsync(Guid organizationId,
        Guid applicationUserId, CancellationToken token)
    {
        await RequireOwnerAsync(organizationId, applicationUserId, token);
        var now = clock.GetUtcNow();
        return (await db.WebHostRegistrations.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.DisplayName).ToListAsync(token)).Select(x => new WebHostRegistrationView(
                x.Id, x.DisplayName, x.ProviderInstallationId,
                x.RevokedAt is not null ? "Revoked" : x.ExpiresAt <= now ? "Expired" : x.Status,
                x.ExpiresAt, x.LastHeartbeatAt,
                x.RevokedAt is null && x.ExpiresAt > now && x.LastHeartbeatAt > now.AddMinutes(-2),
                false, "CertifiedDispatchNotConfigured")).ToArray();
    }

    public async Task RevokeAsync(Guid organizationId, Guid hostId, Guid applicationUserId, CancellationToken token)
    {
        await RequireOwnerAsync(organizationId, applicationUserId, token);
        var host = await db.WebHostRegistrations.SingleOrDefaultAsync(x => x.Id == hostId && x.OrganizationId == organizationId, token)
            ?? throw new UnauthorizedAccessException("The WebHost is unavailable.");
        if (host.RevokedAt is not null) return;
        host.Status = "Revoked"; host.RevokedAt = clock.GetUtcNow(); host.Revision++;
        foreach (var job in await db.WebPreviewJobs.Where(x => x.OrganizationId == organizationId && x.WebHostId == hostId &&
            x.Phase != "Stopped" && x.Phase != "Expired" && x.Phase != "Failed" && x.Phase != "Revoked").ToListAsync(token))
        {
            job.Phase = "Stopping"; job.FailureCode = "HostRevoked"; job.AccessReference = null;
            job.UpdatedAt = clock.GetUtcNow(); job.Revision++;
        }
        await db.SaveChangesAsync(token);
    }

    private async Task<OrganizationUser> RequireOwnerAsync(Guid organizationId, Guid applicationUserId, CancellationToken token) =>
        await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId && x.IsActive && x.ArchivedAt == null &&
            x.EmployeeType == EmployeeType.Human && x.PermissionLevel == OrganizationPermissionLevel.Owner, token)
        ?? throw new UnauthorizedAccessException("Only a current human business owner can manage product hosts.");
}
