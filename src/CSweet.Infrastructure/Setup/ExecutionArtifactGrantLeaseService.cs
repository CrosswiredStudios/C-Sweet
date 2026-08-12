using System.Data;
using System.Security.Cryptography;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class ExecutionArtifactGrantLeaseService(
    CSweetDbContext db,
    TimeProvider timeProvider)
{
    public async Task<bool> ClaimAsync(
        Guid nodeId,
        Guid assignmentId,
        long fencingEpoch,
        string artifactDigest,
        string tokenHash,
        string transferHash,
        CancellationToken cancellationToken = default)
    {
        var postgres = string.Equals(db.Database.ProviderName,
            "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        var assignment = postgres
            ? await db.ExecutionWorkloadAssignments
                .FromSqlInterpolated($"SELECT * FROM \"ExecutionWorkloadAssignments\" WHERE \"Id\" = {assignmentId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.ExecutionWorkloadAssignments.SingleOrDefaultAsync(x => x.Id == assignmentId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var authorized = assignment is not null && assignment.ExecutionNodeId == nodeId &&
            assignment.FencingEpoch == fencingEpoch && assignment.LeaseExpiresAt > now &&
            assignment.ArtifactDigest == artifactDigest && assignment.AssignmentTokenHash == tokenHash &&
            assignment.ArtifactGrantConsumedAt is null &&
            (assignment.ArtifactGrantTransferHash is null || assignment.ArtifactGrantTransferHash == transferHash) &&
            assignment.ArtifactGrantInUseUntil.GetValueOrDefault() <= now &&
            assignment.Status is ExecutionAssignmentStatus.Assigned or
                ExecutionAssignmentStatus.Starting or ExecutionAssignmentStatus.Running;
        if (!authorized)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        assignment!.ArtifactGrantTransferHash ??= transferHash;
        assignment.ArtifactGrantInUseUntil = now.AddMinutes(2);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return false;
        }
    }

    public async Task ReleaseAsync(
        Guid assignmentId,
        string transferHash,
        bool consumed,
        CancellationToken cancellationToken = default)
    {
        db.ChangeTracker.Clear();
        var assignment = await db.ExecutionWorkloadAssignments.SingleOrDefaultAsync(
            x => x.Id == assignmentId && x.ArtifactGrantTransferHash == transferHash, cancellationToken);
        if (assignment is null) return;
        assignment.ArtifactGrantInUseUntil = null;
        if (consumed)
        {
            assignment.ArtifactGrantConsumedAt = timeProvider.GetUtcNow();
            assignment.AssignmentTokenHash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        }
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { }
    }
}
