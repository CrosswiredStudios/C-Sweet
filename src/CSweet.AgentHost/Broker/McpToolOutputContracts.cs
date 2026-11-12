using System.Collections;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.WorkManagement;
using CSweet.Infrastructure.Core;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

/// <summary>
/// The result type each platform tool returns to the agent. A tool's output schema is derived from
/// this type, so the gateway never validates a result against a shape someone typed by hand.
/// A tool not listed here returns one JSON object.
/// </summary>
/// <remarks>
/// QFN Games, 2026-11-11: <c>request_project_resource</c> was hand-listed as an array. Every
/// successful request saved the resource request and then failed the gateway's output check, so the
/// Technical Director saw a failure for work that had happened and the build-capacity setup stalled.
/// Declaring the real type here makes that mismatch impossible, and the unit tests compare these
/// types with the SDK's typed clients.
/// </remarks>
internal static class McpToolOutputContracts
{
    internal static readonly IReadOnlyDictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [ResourceCapabilities.Request] = typeof(ResourceRequestState),
        [ResourceCapabilities.Read] = typeof(IReadOnlyList<ResourceRequestState>),
        [ResourceCapabilities.Decide] = typeof(ResourceRequestState),
        [ProjectApprovalGovernance.ReadCapability] = typeof(IReadOnlyList<ProjectApprovalGovernance.Review>),
        // A decision returns the proposal as read back after the decision.
        [ProjectApprovalGovernance.DecideCapability] = typeof(IReadOnlyList<ProjectApprovalGovernance.Review>),
        [WorkBoardActions.Read] = typeof(IReadOnlyList<W.WorkBoardSummary>),
        [WorkSprintActions.Read] = typeof(IReadOnlyList<W.WorkSprint>),
        [W.WorkDeliveryCapabilities.Read] = typeof(IReadOnlyList<W.WorkDeliveryPlanResponse>),
        [W.WorkstreamCapabilityNames.GateReadV1] = typeof(IReadOnlyList<W.WorkstreamGateSummary>),
        [W.DecisionCapabilityNames.ReadV1] = typeof(IReadOnlyList<W.DecisionRecord>),
        [W.DeliveryEvidenceCapabilityNames.ToolchainCatalogReadV2] = typeof(IReadOnlyList<W.EligibleToolchainAdapter>),
        [W.DeliveryEvidenceCapabilityNames.BuildReadV2] = typeof(IReadOnlyList<W.DeliveryBuildV2>),
        [W.DeliveryEvidenceCapabilityNames.ValidationReadV2] = typeof(IReadOnlyList<W.DeliveryValidation>),
        [W.DeliveryEvidenceCapabilityNames.PreviewReadV2] = typeof(IReadOnlyList<W.DeliveryPreviewV2>),
        [W.DeliveryEvidenceCapabilityNames.EvaluationReadV1] = typeof(IReadOnlyList<W.EvaluationSession>),
        [W.DeliveryEvidenceCapabilityNames.ReleaseReadinessReadV1] = typeof(IReadOnlyList<W.ReleaseReadiness>),
        [SourceControlCapabilities.TeamRepositoryOptions] = typeof(IReadOnlyList<W.TeamRepositoryOption>),
        [TaskDeliveryCapabilities.List] = typeof(IReadOnlyList<TaskReviewResult>),
        [ProjectIntakeCapabilities.List] = typeof(IReadOnlyList<ProjectIntakeSummary>),
        [ProjectIntakeCapabilities.AssistanceList] = typeof(IReadOnlyList<ProjectIntakeSummary>),
        [ProjectIntakeCapabilities.Discover] = typeof(IReadOnlyList<ProjectCandidate>),
        // One document when the request names an artifact, otherwise the granted documents.
        [PlatformCapabilities.ArtifactRead] = typeof(ObjectOrList<ArtifactDocument, ArtifactDocumentSummary>),
    };

    private static readonly JsonElement ObjectSchema = Parse("""{ "type": "object" }""");
    private static readonly JsonElement ArraySchema = Parse("""{ "type": "array", "items": { "type": "object" } }""");
    private static readonly JsonElement ObjectOrArraySchema = Parse("""{ "type": ["object", "array"], "items": { "type": "object" } }""");

    /// <summary>Gets the declared result type of a tool; one object when the tool is not listed.</summary>
    internal static Type For(string capability) =>
        Types.TryGetValue(capability, out var type) ? type : typeof(object);

    /// <summary>
    /// Derives the output schema from the declared result type: a list of records for a collection,
    /// otherwise one object. Only the root kind is checked, because the record shapes are owned by
    /// the contract packages and a stricter copy here would drift from them.
    /// </summary>
    internal static JsonElement Schema(string capability)
    {
        var type = For(capability);
        return IsObjectOrList(type) ? ObjectOrArraySchema : IsCollection(type) ? ArraySchema : ObjectSchema;
    }

    internal static bool IsObjectOrList(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ObjectOrList<,>);

    internal static bool IsCollection(Type type) =>
        type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type) && !IsDictionary(type);

    private static bool IsDictionary(Type type) =>
        typeof(IDictionary).IsAssignableFrom(type) ||
        type.GetInterfaces().Append(type).Any(x => x.IsGenericType &&
            (x.GetGenericTypeDefinition() == typeof(IDictionary<,>) || x.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// Declares a tool whose request selects one <typeparamref name="TObject"/> or a list of
/// <typeparamref name="TItem"/>. It is a declaration only and is never serialized.
/// </summary>
internal sealed class ObjectOrList<TObject, TItem>;
