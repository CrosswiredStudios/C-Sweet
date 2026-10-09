using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Setup;
using DomainSession = CSweet.Domain.Communications.AgentCoordinationSession;
using AgentWorkKind = CSweet.Domain.Setup.AgentWorkKind;

namespace CSweet.Infrastructure.Setup;

/// <summary>
/// Input origins recorded by a trusted native builder before enqueue. This is deliberately
/// not deserialized from agent input and does not certify retained results, private memory,
/// document permissions, legal holds, or permission to erase independent primary artifacts.
/// </summary>
internal sealed class NativeWorkInputEvidence
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Guid organization, installation, session, conversation, board, item, stage, sprint;
    private readonly long revision, assignmentRevision;
    private readonly int ordinal;
    private readonly Guid[] contributors, turns;
    private readonly string payloadHash;

    private NativeWorkInputEvidence(DomainSession source, AgentCoordinationTurnRequest request)
    {
        organization = source.OrganizationId; installation = request.Self.AgentInstallationId;
        session = source.Id; conversation = source.ConversationId;
        board = source.SourceBoardId!.Value; item = source.SourceWorkItemId!.Value;
        stage = source.SourceStageExecutionId!.Value; sprint = source.SourceSprintExecutionId!.Value;
        revision = source.Revision; assignmentRevision = source.SourceAssignmentRevision!.Value;
        ordinal = source.NextTurnOrdinal;
        contributors = new[] { source.InitiatorOrganizationUserId, source.TargetOrganizationUserId }.Order().ToArray();
        turns = request.Transcript.Select(x => x.Id).Order().ToArray();
        // Match inbox serialization of the delivered JsonElement. Typed values such as
        // timestamps can use different escaping from a subsequent JSON element write.
        payloadHash = Digest(JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.SerializeToElement(request, JsonOptions)));
    }

    internal static NativeWorkInputEvidence Coordination(DomainSession source, AgentCoordinationTurnRequest request)
    {
        var canonical = request.WorkSource;
        var people = new[] { source.InitiatorOrganizationUserId, source.TargetOrganizationUserId };
        var installations = new[] { source.InitiatorInstallationId, source.TargetInstallationId };
        if (source.SourceKind != "WorkItem" || request.SourceKind != "WorkItem" || canonical is null ||
            source.SourceConversationId is not null || source.SourceChatTurnId is not null || source.SourceMessageId is not null ||
            source.Id == Guid.Empty || source.OrganizationId == Guid.Empty || source.ConversationId == Guid.Empty ||
            people.Any(x => x == Guid.Empty) || people.Distinct().Count() != 2 ||
            installations.Any(x => x == Guid.Empty) || installations.Distinct().Count() != 2 ||
            canonical.BoardId == Guid.Empty || canonical.ItemId == Guid.Empty || canonical.StageExecutionId == Guid.Empty ||
            canonical.SprintExecutionId == Guid.Empty || canonical.AssignmentRevision < 0 ||
            source.SourceBoardId != canonical.BoardId || source.SourceWorkItemId != canonical.ItemId ||
            source.SourceStageExecutionId != canonical.StageExecutionId || source.SourceSprintExecutionId != canonical.SprintExecutionId ||
            source.SourceAssignmentRevision != canonical.AssignmentRevision || request.BoardSource is not null ||
            request.SessionId != source.Id || request.ExpectedRevision != source.Revision || source.Revision < 1 ||
            request.TurnOrdinal != source.NextTurnOrdinal || source.NextTurnOrdinal < 1 ||
            request.Subject != source.Subject || request.Objective != source.Objective || request.IsFinalization != source.IsFinalization ||
            request.MaximumTurns != source.MaximumTurns || source.CurrentOrganizationUserId != request.Self.OrganizationUserId ||
            !people.Contains(request.Self.OrganizationUserId) || !people.Contains(request.Counterpart.OrganizationUserId) ||
            request.Self.OrganizationUserId == request.Counterpart.OrganizationUserId ||
            request.Self.AgentInstallationId != InstallationFor(source, request.Self.OrganizationUserId) ||
            request.Counterpart.AgentInstallationId != InstallationFor(source, request.Counterpart.OrganizationUserId) ||
            !request.SuccessCriteria.SequenceEqual(JsonSerializer.Deserialize<string[]>(source.SuccessCriteriaJson, JsonOptions) ?? []) ||
            request.Transcript.Count is < 1 or > 64 || request.Transcript.Count != source.Turns.Count ||
            request.Transcript.Select(x => x.Id).Distinct().Count() != request.Transcript.Count ||
            request.Transcript.Any(x => x.Id == Guid.Empty || !people.Contains(x.SpeakerOrganizationUserId) ||
                !source.Turns.Any(t => t.Id == x.Id && t.SessionId == source.Id && t.Ordinal == x.Ordinal &&
                    t.SpeakerOrganizationUserId == x.SpeakerOrganizationUserId && t.Content == x.Content &&
                    t.Disposition == x.Disposition && t.CreatedAt == x.CreatedAt && t.ArtifactDigest == x.Artifact?.Digest)))
            throw new InvalidOperationException("Native collaboration input origins do not match the server-built turn.");
        return new(source, request);
    }

    internal void RequirePayload(string hash, string org, Guid target, AgentWorkKind kind, string name,
        string? correlation, string? sourceType, string? sourceId)
    {
        if (hash != payloadHash || org != organization.ToString("D") || target != installation || kind != AgentWorkKind.Event ||
            name != AgentCoordinationEvents.TurnRequested || correlation != session.ToString("D") || sourceType != "agent-coordination" ||
            !Guid.TryParseExact(sourceId, "D", out var eventId) || eventId == Guid.Empty)
            throw new InvalidOperationException("Native collaboration input evidence is bound to another payload or consumer.");
    }

    internal string Bind(AgentWorkItem work)
    {
        RequirePayload(work.PayloadHash, work.OrganizationId, work.AgentInstallationId, work.Kind, work.Name,
            work.CorrelationId, work.SourceType, work.SourceId);
        return JsonSerializer.Serialize(new
        {
            version = 1, builder = "canonical-case-coordination-v1", workId = work.Id,
            organizationId = organization, installationId = installation,
            payloadHash, protectedPayloadHash = Digest(work.ProtectedPayload),
            kind = work.Kind.ToString(), work.Name, work.CorrelationId, work.CausationId,
            work.SourceType, work.SourceId, idempotencyHash = Digest(System.Text.Encoding.UTF8.GetBytes(work.IdempotencyKey)),
            origins = new { caseId = item, boardId = board, stageId = stage, sprintId = sprint,
                assignmentRevision, sessionId = session, conversationId = conversation, sessionRevision = revision,
                turnOrdinal = ordinal, contributors, turnIds = turns },
            coverage = "input-origins-only-v1"
        }, JsonOptions);
    }

    private static Guid InstallationFor(DomainSession source, Guid person) => person == source.InitiatorOrganizationUserId
        ? source.InitiatorInstallationId : person == source.TargetOrganizationUserId ? source.TargetInstallationId : Guid.Empty;
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
