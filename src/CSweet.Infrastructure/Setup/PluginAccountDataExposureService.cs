using System.Text.Json;
using CSweet.Infrastructure.Persistence;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Propagates recorded provenance only after a host handler authorizes and constructs a response.</summary>
public sealed class PluginAccountDataExposureService(CSweetDbContext db)
{
    public async Task RecordResponseAsync(Guid organizationId,Guid consumer,ReadOnlyMemory<byte> response,CancellationToken ct)
    {
        if(!await db.AgentInstallations.AnyAsync(x=>x.Id==consumer && x.BusinessId==organizationId.ToString("D") && x.IsEnabled && x.RevisionStatus==PluginRevisionStatus.Active,ct))
            throw new UnauthorizedAccessException("The data consumer is no longer active.");
        if(response.Length==0) return;
        if(!await db.PluginAccountDataCopies.AnyAsync(x=>x.OrganizationId==organizationId && x.DeletedAt==null && !x.Store.StartsWith("external:") && x.Store!="consumer-origin",ct)) return;
        if(response.Length>4*1024*1024) throw new InvalidOperationException("Use a bounded response before receiving retained provider data.");
        using var document=JsonDocument.Parse(response,new JsonDocumentOptions{MaxDepth=32});
        var ids=new HashSet<Guid>();
        void Read(JsonElement value)
        {
            if(value.ValueKind==JsonValueKind.String && Guid.TryParse(value.GetString(),out var id)) ids.Add(id);
            else if(value.ValueKind==JsonValueKind.Object) foreach(var field in value.EnumerateObject()) Read(field.Value);
            else if(value.ValueKind==JsonValueKind.Array) foreach(var item in value.EnumerateArray()) Read(item);
            if(ids.Count>512) throw new InvalidOperationException("Use a smaller authorized response page.");
        }
        Read(document.RootElement);
        if(ids.Count==0) return;
        var accounts=await db.PluginAccountDataCopies.AsNoTracking().Where(x=>x.OrganizationId==organizationId && ids.Contains(x.RecordId) &&
            x.DeletedAt==null && !x.Store.StartsWith("external:") && x.Store!="consumer-origin").Select(x=>x.AccountGroupId).Distinct().ToArrayAsync(ct);
        if(accounts.Length==0) return;
        await using var tx=db.Database.IsRelational() && db.Database.CurrentTransaction==null?await db.Database.BeginTransactionAsync(ct):null;
        foreach(var account in accounts)
        {
            var group=db.Database.IsNpgsql()
                ? (await db.PluginAccountGroups.FromSqlInterpolated($"SELECT * FROM \"PluginAccountGroups\" WHERE \"Id\" = {account} FOR SHARE").AsNoTracking().ToArrayAsync(ct)).Single()
                : await db.PluginAccountGroups.AsNoTracking().SingleAsync(x=>x.Id==account,ct);
            if(group.State!=PluginAccountState.Connected) throw new UnauthorizedAccessException("The requested provider-derived data is being erased.");
            foreach(var store in new[]{"consumer-origin","external:runtime","external:processors","external:retained-evidence"}) await db.TrackCopy(account,organizationId,consumer,store,consumer,ct);
        }
        await db.SaveChangesAsync(ct);
        if(tx!=null) await tx.CommitAsync(ct);
    }
}
