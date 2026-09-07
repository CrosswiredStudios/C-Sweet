from pathlib import Path
roots=[Path('../CSweet.Agent.Producer.VideoGame'),Path('../CSweet.Agent.CreativeDirector.VideoGame')]
for root in roots:
    p=root/'src'/root.name/'PitchProtocol.cs';s=p.read_text(encoding='utf-8')
    s=s.replace('internal sealed record PitchBrief(GameVisionBrief Vision, Guid PitchArtifactId, Guid PitchRevisionId, string PitchSha256);','''internal sealed record DocumentReference(Guid DocumentId, Guid RevisionId, string ContentSha256);
internal sealed record PitchBrief(GameVisionBrief Vision, Guid PitchArtifactId, Guid PitchRevisionId, string PitchSha256)
{
    public IReadOnlyList<DocumentReference> DocumentReferences => new[]
    {
        new DocumentReference(PitchArtifactId, PitchRevisionId, PitchSha256),
        new DocumentReference(Vision.HighLevelGddArtifactId!.Value, Vision.HighLevelGddAcceptedRevisionId!.Value, Vision.HighLevelGddRevisionSha256!)
    }.DistinctBy(x => x.DocumentId).ToArray();
}''')
    s=s.replace('bool Ready, IReadOnlyList<string> Questions, string Rationale);','''bool Ready, IReadOnlyList<string> Questions, string Rationale)
{
    public IReadOnlyList<DocumentReference> DocumentReferences => [new(DocumentId, RevisionId, RevisionSha256)];
}''',1)
    p.write_text(s,encoding='utf-8')
p=roots[0]/'src'/roots[0].name/'SpecialistAgent.cs';s=p.read_text(encoding='utf-8').replace('request.WorkContext!.WorkstreamId!.Value','request.WorkContext!.WorkstreamId');p.write_text(s,encoding='utf-8')
p=roots[0]/'src'/roots[0].name/'PitchRefinement.cs';s=p.read_text(encoding='utf-8')
s=s.replace('request.Transcript.FirstOrDefault(x => x.Artifact?.Type == PitchProtocol.BriefType)', 'request.Transcript.FirstOrDefault(x => x.SpeakerOrganizationUserId == request.Counterpart.OrganizationUserId && x.Artifact?.Type == PitchProtocol.BriefType)')
s=s.replace('        if (reply?.Accepted == true)', '''        if (reply is not null && (reply.PitchDigest != initial.Key || previous is null || reply.DocumentId != previous.DocumentId ||
            reply.RevisionId != previous.RevisionId || reply.RevisionSha256 != previous.RevisionSha256))
            return AgentCoordinationTurnResult.Blocked("The Director's reply must match this Producer draft and pitch.");
        if (reply?.Accepted == true)''')
s=s.replace('        var review = await PitchProtocol.CachedAsync(','''        var priorDraft = previous is null ? null : await context.Platform.Artifacts.GetAsync(previous.DocumentId, token);
        var priorContent = priorDraft?.Revisions.Single(x => x.Id == previous!.RevisionId).Content ?? "No draft yet.";
        var review = await PitchProtocol.CachedAsync(''')
s=s.replace('Accepted GDD:\\n{gdd.Revision.Content}\\nConversation:', 'Accepted GDD:\\n{gdd.Revision.Content}\\nCurrent shared draft:\\n{priorContent}\\nConversation:')
p.write_text(s,encoding='utf-8')
