using CSweet.WebHost.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class WebPreviewExecutionService
{
    public async Task<PreviewPage> ListAsync(Guid organization, Guid installation, ListPreviewsRequest request, CancellationToken token)
    {
        if (request.ProjectId == Guid.Empty || request.Limit is < 1 or > 100 || request.AfterId == Guid.Empty)
            throw new ArgumentException("Choose a project and a page size from one to one hundred.");
        var actor = await grants.RequireActorAsync(organization, installation, WebPreviewCapabilities.List, token);
        await grants.RequireWorkstreamAsync(organization, actor.Id, request.ProjectId, token);
        // Include terminal states and disabled hosting providers, so a wake can discover missed failures/expiry.
        // These rows remain authoritative after notification delivery and diagnostic retention expire.
        var query = db.WebPreviewJobs.AsNoTracking().Where(x => x.OrganizationId == organization && x.InstallationId == installation && x.WorkstreamId == request.ProjectId);
        if (request.AfterId is { } after) query = query.Where(x => x.Id.CompareTo(after) > 0);
        var rows = await query.OrderBy(x => x.Id).Take(request.Limit + 1).ToListAsync(token);
        return new(rows.Take(request.Limit).Select(Map).ToArray(), rows.Count > request.Limit ? rows[request.Limit - 1].Id : null);
    }
}
