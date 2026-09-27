using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class McpSprintPlanningSchemaTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EstimateAcceptsTypedProvenanceAndLegacyRequests(bool withProvenance)
    {
        var request = Estimate() with { Provenance = withProvenance ? Evidence() : null };
        Validate(WorkItemCapabilities.Estimate, request);
        var legacy = JsonSerializer.SerializeToNode(request, JsonOptions)!.AsObject();
        legacy.Remove("provenance");
        Validate(WorkItemCapabilities.Estimate, legacy);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing-source")]
    [InlineData("missing-digest")]
    [InlineData("empty-digest")]
    [InlineData("invalid-source")]
    [InlineData("invalid-installation")]
    [InlineData("invalid-session")]
    [InlineData("invalid-revision")]
    [InlineData("negative-confidence")]
    [InlineData("excess-confidence")]
    public void EstimateRejectsMalformedEvidence(string scenario)
    {
        var request = JsonSerializer.SerializeToNode(Estimate(), JsonOptions)!.AsObject();
        var provenance = request["provenance"]!.AsObject();
        switch (scenario)
        {
            case "unknown": provenance["approvalOverride"] = true; break;
            case "missing-source": provenance.Remove("sourceOrganizationUserId"); break;
            case "missing-digest": provenance.Remove("sourceDigest"); break;
            case "empty-digest": provenance["sourceDigest"] = ""; break;
            case "invalid-source": provenance["sourceOrganizationUserId"] = "invalid"; break;
            case "invalid-installation": provenance["sourceAgentInstallationId"] = "invalid"; break;
            case "invalid-session": provenance["coordinationSessionId"] = "invalid"; break;
            case "invalid-revision": provenance["coordinationTurnRevision"] = 0; break;
            case "negative-confidence": provenance["confidence"] = -0.1m; break;
            case "excess-confidence": provenance["confidence"] = 1.1m; break;
        }
        Assert.ThrowsAny<Exception>(() => Validate(WorkItemCapabilities.Estimate, request));
    }

    [Fact]
    public void RemainingProducerSprintPreparationRequestsMatchBrokerSchemas()
    {
        var board = Guid.NewGuid();
        var item = Guid.NewGuid();
        var sprint = Guid.NewGuid();
        Validate(WorkItemCapabilities.Comment, new CommentOnWorkItemRequest(board, item, "QA readiness evidence", "evidence")
        {
            Kind = "qa-sprint-readiness", CoordinationSessionId = Guid.NewGuid(), ArtifactDigest = new string('a', 64)
        });
        Validate(WorkItemCapabilities.Move, new MoveWorkItemRequest(board, item, Guid.NewGuid(), 1, "ready"));
        Validate(WorkSprintCapabilities.ManageScope, new SetWorkItemSprintRequest(board, item, sprint, 1, "scope"));
        Validate(WorkSprintCapabilities.ManageCapacity, new SetWorkSprintCapacityRequest(board, sprint, 13, 1, "capacity"));
        Validate(WorkOrchestrationCapabilities.Preflight, new StartWorkSprintExecutionRequest(board, sprint, 1, "preflight"));
        Validate(WorkOrchestrationCapabilities.Start, new StartWorkSprintExecutionRequest(board, sprint, 1, "start"));
    }

    private static EstimateWorkItemRequest Estimate() => new(Guid.NewGuid(), Guid.NewGuid(), 5, 1, "estimate")
        { Provenance = Evidence() };
    private static WorkEstimateProvenance Evidence() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, new string('a', 64), 0.7m);
    private static void Validate(string capability, object request)
    {
        var tool = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { capability }));
        JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(request, JsonOptions), tool.InputSchema);
    }
}
