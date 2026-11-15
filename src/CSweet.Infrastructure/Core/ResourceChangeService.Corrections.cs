using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class ResourceChangeService
{
    private async Task<StaffingCorrection?> ValidateCorrectionAsync(Guid organization, Guid requester,
        ResourceChangeRequestRecord? previous, Guid? correctionId, CancellationToken token)
    {
        var pending = previous is null ? null : await db.StaffingCorrections.SingleOrDefaultAsync(x =>
            x.OrganizationId == organization && x.SourceRequestId == previous.Id && x.Status == "Pending", token);
        if (pending is null && correctionId is null) return null;
        if (pending is null || pending.Id != correctionId || previous!.RequesterOrganizationUserId != requester ||
            previous.Status != ResourceChangeRequestStatus.Approved)
            throw new InvalidOperationException("Re-read the pending staffing correction and submit a revision bound to its current approved source.");
        if (pending.ReplacementRequestId is { } replacement && await db.ResourceChangeRequests.AnyAsync(x => x.Id == replacement && x.Status == ResourceChangeRequestStatus.Pending, token))
            throw new InvalidOperationException("The correction already has a proposal awaiting manager review.");
        return pending;
    }

    private async Task<IReadOnlyList<StaffingCorrectionResponse>> ReadCorrectionsAsync(Guid organization, Guid actor,
        Guid? sourceId, CancellationToken token)
    {
        var rows = await (from correction in db.StaffingCorrections.AsNoTracking()
            join source in db.ResourceChangeRequests.AsNoTracking() on correction.SourceRequestId equals source.Id
            where correction.OrganizationId == organization && source.OrganizationId == organization && correction.Status == "Pending" &&
                (source.RequesterOrganizationUserId == actor || source.ManagerOrganizationUserId == actor) &&
                (sourceId == null || source.Id == sourceId || correction.ReplacementRequestId == sourceId)
            orderby correction.CreatedAt
            select new { Correction = correction, Source = source }).Take(100).ToListAsync(token);
        return rows.Select(x => StaffingCorrectionService.Map(x.Correction, x.Source)).ToArray();
    }
}
