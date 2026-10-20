using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

public sealed class ComputeProviderRetirementService(CSweetDbContext db, IDataProtectionProvider protection)
    : IBusinessComputeStorageCleanup
{
    private readonly IDataProtector protector = protection.CreateProtector("CSweet.Compute.RetirementReceipt.v1");
    private sealed record ReceiptState(string ProtectedToken, string Hash);

    public async Task<ComputeProviderRetirement> PrepareAsync(Guid business, Guid setup, CancellationToken token)
    {
        var job = await ResourceCleanupQueue.EnqueueAsync(db, ResourceCleanupQueue.LocalCompute, setup, business, DateTimeOffset.UtcNow, token);
        if (job.PayloadJson == "{}")
        {
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            job.PayloadJson = JsonSerializer.Serialize(new ReceiptState(protector.Protect(secret), Hash(secret)));
        }
        await db.SaveChangesAsync(token);
        var state = JsonSerializer.Deserialize<ReceiptState>(job.PayloadJson)!;
        return new(job.Id, business, setup, protector.Unprotect(state.ProtectedToken));
    }

    public async Task<bool> TryCleanupAsync(Guid businessId, CancellationToken token)
    {
        var setups = await db.Set<CSweet.Domain.Compute.ComputeLocalSetup>().AsNoTracking()
            .Where(x => x.OrganizationId == businessId).ToListAsync(token);
        foreach (var setup in setups)
        {
            var id = setup.Id;
            var job = await db.ResourceCleanupRequests.SingleOrDefaultAsync(x => x.Kind == ResourceCleanupQueue.LocalCompute && x.ResourceId == id, token);
            if (job?.CompletedAt is not null) continue;
            if (job is not null) continue;
            // A setup that never installed a provider has only user-owned staging to clean.
            var enrolled = await db.ComputeNodes.AnyAsync(x => x.OrganizationId == businessId && x.Id == id, token);
            if (!enrolled)
            {
                // Absence of enrollment is not evidence that an interrupted privileged installer left no files.
                if (setup.PreparationAttemptId is not null) return false;
                continue;
            }
            await PrepareAsync(businessId, id, token);
        }
        // Re-read rather than treating the absence of an enrollment as unconfirmed cleanup.
        if (await db.ResourceCleanupRequests.AnyAsync(x => x.Kind == ResourceCleanupQueue.LocalCompute &&
            x.BusinessId == businessId && x.CompletedAt == null, token)) return false;
        var setupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSweet", "ComputeSetup");
        foreach (var setup in setups)
            CSweet.TrustedServices.OwnedStorage.DeleteDirectory(setupRoot, CSweet.TrustedServices.OwnedStorage.Child(setupRoot, setup.Id.ToString("N")), token);
        return true;
    }

    public async Task<ComputeProviderRetirement?> ReadAsync(ComputeProviderWorkRequest node, CancellationToken token)
    {
        if (node.ProviderId != "hyperv" ||
            !await db.CoreOrganizations.AnyAsync(x => x.Id == node.OrganizationId && x.Status == OrganizationStatus.Deleting, token) ||
            await db.ComputeEnvironments.AnyAsync(x => x.OrganizationId == node.OrganizationId && x.TeardownConfirmedAt == null, token)) return null;
        var job = await db.ResourceCleanupRequests.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Kind == ResourceCleanupQueue.LocalCompute && x.ResourceId == node.NodeId && x.BusinessId == node.OrganizationId && x.CompletedAt == null, token);
        if (job is null || !await CanRetireAsync(job, token)) return null;
        var state = JsonSerializer.Deserialize<ReceiptState>(job.PayloadJson) ?? throw new InvalidDataException("Retirement receipt state is missing.");
        return new(job.Id, job.BusinessId, job.ResourceId, protector.Unprotect(state.ProtectedToken));
    }

    public async Task ConfirmAsync(ComputeProviderRetirementReceipt receipt, CancellationToken token)
    {
        var job = await RequireReceiptAsync(receipt, token);
        if (!await CanRetireAsync(job, token)) throw new InvalidOperationException("Business teardown is not ready for provider retirement.");
        job.CompletedAt ??= DateTimeOffset.UtcNow;
        job.LastError = null;
        await db.SaveChangesAsync(token);
    }

    public async Task<bool> AuthorizeAsync(ComputeProviderRetirementReceipt receipt, CancellationToken token) =>
        await CanRetireAsync(await RequireReceiptAsync(receipt, token), token);

    private async Task<bool> CanRetireAsync(CSweet.Domain.Setup.ResourceCleanupRequest job, CancellationToken token)
    {
        if (job.CompletedAt is not null) return true; // Lost callback response, including after the business purge.
        return await db.CoreOrganizations.AnyAsync(x => x.Id == job.BusinessId && x.Status == OrganizationStatus.Deleting, token) &&
            !await db.Set<CSweet.Domain.Compute.ComputeLocalSetup>().AnyAsync(x => x.Id == job.ResourceId && x.State == "Running", token) &&
            !await db.ComputeEnvironments.AnyAsync(x => x.OrganizationId == job.BusinessId && x.TeardownConfirmedAt == null, token) &&
            await db.ResourceCleanupRequests.AnyAsync(x => x.BusinessId == job.BusinessId && x.Kind == ResourceCleanupQueue.BusinessStorage, token);
    }

    private async Task<CSweet.Domain.Setup.ResourceCleanupRequest> RequireReceiptAsync(ComputeProviderRetirementReceipt receipt, CancellationToken token)
    {
        if (receipt.ReceiptToken is not { Length: 64 } || receipt.ReceiptToken.Any(x => !Uri.IsHexDigit(x)))
            throw new UnauthorizedAccessException("Invalid retirement receipt.");
        var job = await db.ResourceCleanupRequests.SingleOrDefaultAsync(x => x.Id == receipt.CleanupId && x.Kind == ResourceCleanupQueue.LocalCompute, token)
            ?? throw new UnauthorizedAccessException("Unknown retirement receipt.");
        var state = JsonSerializer.Deserialize<ReceiptState>(job.PayloadJson)!;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(state.Hash), Encoding.ASCII.GetBytes(Hash(receipt.ReceiptToken))))
            throw new UnauthorizedAccessException("Invalid retirement receipt.");
        if (await db.ComputeEnvironments.AnyAsync(x => x.OrganizationId == job.BusinessId && x.TeardownConfirmedAt == null, token))
            throw new InvalidOperationException("Compute workloads are not torn down.");
        return job;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)));
}
