using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    private bool providerErasure;
    internal IDisposable BeginProviderDataErasure() { var prior=providerErasure; providerErasure=true; return new ErasureScope(()=>providerErasure=prior); }
    private sealed class ErasureScope(Action restore) : IDisposable { public void Dispose()=>restore(); }
    // A platform-owned conservative classification: derived agent records inherit the sources of
    // its explicit account bindings. No text search, caller-supplied parent IDs or provider-specific fields.
    internal static readonly IReadOnlyDictionary<string,string[]> ProviderContentFields = new Dictionary<string,string[]>
    {
        ["PluginOperationalState"]=["PayloadJson"], ["ConversationMessage"]=["Content"],
        ["MemoryEpisodeEnrichmentJob"]=["SourceJson","AcceptedExtractionJson"],
        ["MemoryCaptureOutboxItem"]=["AcceptedExtractionJson"],
        ["Artifact"]=["Title","Content"], ["ArtifactRevision"]=["Content"],
        ["ActionProposal"]=["Title","Description","PayloadJson","Summary","DecisionComment"],
        ["AgentWorkItem"]=["ProtectedPayload","ProtectedResult","MemoryRecallReceiptJson","NativeWorkInputReceiptJson","LastError"],
        ["AgentWorkAttempt"]=["Error"], ["AgentWorkProgress"]=["ProtectedValue"],
        ["AgentRunLog"]=["PromptPreview","OutputPreview","FailureMessage","InferenceSettingsJson"], ["AgentRuntimeEvent"]=["Reason","PayloadJson"],
        ["WorkTask"]=["Title","Description","ResultSummary","BlockReason","WaitingReason","PersonalWorkContextJson","StructuredMentionsJson","DevelopmentBriefJson","PlanningSpecificationJson","EstimateProvenanceJson","DeliverySpecificationJson","QualityBriefJson"],
        ["ChatTurn"]=["PartialResponse","ErrorMessage"], ["ChatTurnTraceEvent"]=["Title","Summary","DetailsJson"],
        ["SuggestedUserAction"]=["Label","Description","ParametersJson"],
        ["ApplicationRealtimeOutboxItem"]=["DataJson","LastError"], ["AgentPlatformEventOutboxItem"]=["DataJson","LastError"],
        ["CommunicationEventOutboxItem"]=["DataJson","LastError"], ["ConnectorExecution"]=["PlanJson","ResultJson","ResourceId"]
    };
    private static bool IsProviderWrite(EntityEntry entry) => ProviderContentFields.TryGetValue(entry.Metadata.ClrType.Name,out var fields) &&
        (entry.State == EntityState.Added || entry.State == EntityState.Modified &&
        (entry.Entity is not (CSweet.Domain.Core.MemoryEpisodeEnrichmentJob or CSweet.Domain.Core.MemoryCaptureOutboxItem) || fields.Any(f=>entry.Property(f).IsModified)));
    private bool HasProviderDataWrites() => ChangeTracker.Entries().Any(IsProviderWrite);

    private async Task CaptureProviderDataAsync(CancellationToken ct=default)
    {
        if(providerErasure) return;
        var entries=ChangeTracker.Entries().Where(IsProviderWrite).ToArray();
        foreach(var entry in entries)
        {
            Guid? Value(string key) => entry.Metadata.FindProperty(key)?.ClrType==typeof(Guid) || entry.Metadata.FindProperty(key)?.ClrType==typeof(Guid?)
                ? entry.Property(key).CurrentValue as Guid? : null;
            var actor=Value("AgentInstallationId") ?? Value("RequesterInstallationId") ?? Value("CreatedByAgentInstallationId") ?? Value("OriginatingInstallationId");
            if(entry.Entity is CSweet.Domain.Core.MemoryEpisodeEnrichmentJob memoryJob) actor=memoryJob.InstallationId;
            if(entry.Entity is CSweet.Domain.Core.MemoryCaptureOutboxItem captureJob)
            {
                var conversationId=CoreConversationMessages.Local.FirstOrDefault(m=>m.Id==captureJob.ConversationMessageId)?.ConversationId ??
                    await CoreConversationMessages.AsNoTracking().Where(m=>m.Id==captureJob.ConversationMessageId).Select(m=>(Guid?)m.ConversationId).SingleOrDefaultAsync(ct);
                var employeeId=CoreConversations.Local.FirstOrDefault(c=>c.Id==conversationId)?.AgentOrganizationUserId ??
                    await CoreConversations.AsNoTracking().Where(c=>c.Id==conversationId).Select(c=>c.AgentOrganizationUserId).SingleOrDefaultAsync(ct);
                actor=CoreOrganizationUsers.Local.FirstOrDefault(u=>u.Id==employeeId)?.AgentInstallationId ??
                    await CoreOrganizationUsers.AsNoTracking().Where(u=>u.Id==employeeId).Select(u=>u.AgentInstallationId).SingleOrDefaultAsync(ct);
            }
            if(actor==null && (Value("SenderOrganizationUserId") ?? Value("CreatedByOrganizationUserId") ?? Value("TargetAgentOrganizationUserId")) is { } employee)
                actor=CoreOrganizationUsers.Local.FirstOrDefault(u=>u.Id==employee)?.AgentInstallationId ??
                    await CoreOrganizationUsers.AsNoTracking().Where(u=>u.Id==employee).Select(u=>u.AgentInstallationId).SingleOrDefaultAsync(ct);
            if(actor==null && Value("AgentWorkItemId") is { } workId)
                actor=AgentWorkItems.Local.FirstOrDefault(w=>w.Id==workId)?.AgentInstallationId ??
                    await AgentWorkItems.AsNoTracking().Where(w=>w.Id==workId).Select(w=>(Guid?)w.AgentInstallationId).SingleOrDefaultAsync(ct);
            if(actor==null && Value("AgentRuntimeInstanceId") is { } runtimeId)
                actor=AgentRuntimeInstances.Local.FirstOrDefault(r=>r.Id==runtimeId)?.AgentInstallationId ??
                    await AgentRuntimeInstances.AsNoTracking().Where(r=>r.Id==runtimeId).Select(r=>(Guid?)r.AgentInstallationId).SingleOrDefaultAsync(ct);
            if(actor==null && Value("ChatTurnId") is { } turnId)
            {
                var owner=ChatTurns.Local.FirstOrDefault(t=>t.Id==turnId)?.TargetAgentOrganizationUserId ??
                    await ChatTurns.AsNoTracking().Where(t=>t.Id==turnId).Select(t=>(Guid?)t.TargetAgentOrganizationUserId).SingleOrDefaultAsync(ct);
                if(owner is { } target) actor=CoreOrganizationUsers.Local.FirstOrDefault(u=>u.Id==target)?.AgentInstallationId ??
                    await CoreOrganizationUsers.AsNoTracking().Where(u=>u.Id==target).Select(u=>u.AgentInstallationId).SingleOrDefaultAsync(ct);
            }
            if(actor==null) continue;
            var organization=await AgentInstallations.AsNoTracking().Where(x=>x.Id==actor).Select(x=>x.BusinessId).SingleOrDefaultAsync(ct);
            if(!Guid.TryParse(organization,out var org)) continue;
            var groups=await AgentCapabilityBindings.AsNoTracking().Where(b=>b.RequesterInstallationId==actor && b.OrganizationId==organization && b.RevokedAt==null && b.PluginAccountResourceId!=null)
                .Join(PluginAccountResources,b=>b.PluginAccountResourceId,r=>(Guid?)r.Id,(b,r)=>r.AccountGroupId).Distinct().ToArrayAsync(ct);
            var prior=await PluginAccountDataCopies.AsNoTracking().Where(x=>x.ConsumerInstallationId==actor && x.Store=="consumer-origin" && x.DeletedAt==null)
                .Select(x=>x.AccountGroupId).ToArrayAsync(ct);
            foreach(var accountId in groups.Concat(prior).Distinct())
            {
                var group=Database.IsNpgsql() && Database.CurrentTransaction!=null
                    ? (await PluginAccountGroups.FromSqlInterpolated($"SELECT * FROM \"PluginAccountGroups\" WHERE \"Id\" = {accountId} FOR SHARE").AsNoTracking().ToArrayAsync(ct)).SingleOrDefault()
                    : await PluginAccountGroups.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==accountId,ct);
                if(group==null) continue;
                if(group.State is PluginAccountState.Disconnecting or PluginAccountState.Disconnected)
                    throw new InvalidOperationException("This account's derived data is being erased. Restart with current authority before writing new output.");
                if(Value("Id") is not { } id) continue;
                // Capture jobs derive from a source; they do not make human-authored message bodies
                // disposable company data. Keep only a content-free lineage marker for that source.
                if(entry.Entity is CSweet.Domain.Core.MemoryCaptureOutboxItem captureSource)
                    await TrackCopy(accountId,org,actor.Value,"memory-origin",captureSource.ConversationMessageId,ct);
                else await TrackCopy(accountId,org,actor.Value,entry.Metadata.ClrType.Name,id,ct);
                await TrackCopy(accountId,org,actor.Value,"consumer-origin",actor.Value,ct);
                // These sinks must confirm erasure through deployment-owned adapters. Absence of an
                // adapter cannot silently turn into a successful deletion receipt.
                await TrackCopy(accountId,org,actor.Value,"external:runtime",actor.Value,ct);
                await TrackCopy(accountId,org,actor.Value,"external:processors",actor.Value,ct);
                await TrackCopy(accountId,org,actor.Value,"external:retained-evidence",actor.Value,ct);
                if(entry.Entity is CSweet.Domain.Core.MemoryEpisodeEnrichmentJob episodeJob)
                    await TrackCopy(accountId,org,actor.Value,"memory-origin",episodeJob.EpisodeId,ct);
                if(entry.Metadata.ClrType.Name is "ConversationMessage" or "ChatTurn" or "MemoryEpisodeEnrichmentJob" or "MemoryCaptureOutboxItem")
                    await TrackCopy(accountId,org,actor.Value,"external:memory",actor.Value,ct);
            }
        }
    }
    internal async Task TrackCopy(Guid account,Guid? organization,Guid actor,string store,Guid record,CancellationToken ct)
    {
        var existing=PluginAccountDataCopies.Local.FirstOrDefault(x=>x.AccountGroupId==account && x.Store==store && x.RecordId==record) ??
            await PluginAccountDataCopies.SingleOrDefaultAsync(x=>x.AccountGroupId==account && x.Store==store && x.RecordId==record,ct);
        if(existing==null) PluginAccountDataCopies.Add(new(){Id=Guid.NewGuid(),AccountGroupId=account,OrganizationId=organization,
            ConsumerInstallationId=actor,Store=store,RecordId=record,CreatedAt=ExecutionClock.GetUtcNow()});
        else { existing.DeletedAt=null; existing.CreatedAt=ExecutionClock.GetUtcNow(); existing.Revision++; }
    }
}
