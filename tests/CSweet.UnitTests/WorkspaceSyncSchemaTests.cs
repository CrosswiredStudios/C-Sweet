using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;

namespace CSweet.UnitTests;

public sealed class WorkspaceSyncSchemaTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static JsonElement Schema => Assert.Single(new McpToolCatalog([]).List(
        new HashSet<string> { GitWorkspaceCapabilities.Sync })).InputSchema;

    [Fact]
    public void Sdk_materialization_request_with_null_checkpoint_fields_passes_gateway_schema()
    {
        // Same typed request and serializer as the failed SDK MaterializeAsync call.
        var request = new GitWorkspaceSyncRequest(Guid.NewGuid(), 1, "pull", "workspace:pull:1");
        var payload = JsonSerializer.SerializeToElement(request, WebJson);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("commandClass").ValueKind);
        JsonSchemaValidator.Validate(payload, Schema);
        // Older clients omit the optional checkpoint fields altogether.
        JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(new {
            request.WorkspaceId, request.AssignmentRevision, request.Direction, request.IdempotencyKey
        }, WebJson), Schema);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("test")]
    [InlineData("restore")]
    [InlineData("analysis")]
    public void Sdk_checkpoint_upload_preserves_resource_and_resumption_evidence(string commandClass)
    {
        var request = Upload(commandClass);
        JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(request, WebJson), Schema);
    }

    [Theory]
    [InlineData("unknown-field")]
    [InlineData("unknown-command-class")]
    [InlineData("invalid-input-fingerprint")]
    [InlineData("invalid-source-fingerprint")]
    [InlineData("unbounded-validations")]
    [InlineData("unbounded-command")]
    [InlineData("unknown-summary-field")]
    [InlineData("unknown-validation-field")]
    public void Checkpoint_schema_rejects_invalid_or_undeclared_evidence(string mutation)
    {
        var payload = JsonSerializer.SerializeToNode(Upload("build"), WebJson)!;
        var summary = payload["resumeSummary"]!;
        switch (mutation)
        {
            case "unknown-field": payload["hostPath"] = "forged"; break;
            case "unknown-command-class": payload["commandClass"] = "shell"; break;
            case "invalid-input-fingerprint": payload["inputFingerprint"] = "not-a-digest"; break;
            case "invalid-source-fingerprint": summary["sourceFingerprint"] = new string('z', 64); break;
            case "unbounded-validations":
                summary["validations"] = new JsonArray(Enumerable.Range(0, 17)
                    .Select(_ => JsonSerializer.SerializeToNode(new WorkspaceValidationReceipt("npm test", true, 0), WebJson)).ToArray());
                break;
            case "unbounded-command": summary["validations"]![0]!["command"] = new string('x', 513); break;
            case "unknown-summary-field": summary["grant"] = "forged"; break;
            case "unknown-validation-field": summary["validations"]![0]!["hostPath"] = "forged"; break;
        }
        Assert.ThrowsAny<Exception>(() => JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(payload), Schema));
    }

    [Fact]
    public void Sdk_git_checkpoint_and_resume_requests_pass_gateway_schema()
    {
        // The exact requests SDK 5.20.0 sends for a workspace cloned with git.
        var workspace = Guid.NewGuid(); var pushed = new string('c', 40);
        JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(
            new GitWorkspaceSyncRequest(workspace, 1, "resume", $"workspace:{workspace:N}:resume:1"), WebJson), Schema);
        JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(
            new GitWorkspaceSyncRequest(workspace, 1, "checkpoint", $"workspace:{workspace:N}:{pushed}")
            {
                PushedCommitSha = pushed, CommandClass = "build", InputFingerprint = new string('a', 64),
                ResumeSummary = new(new string('b', 64), [new("npm test", true, 0)])
            }, WebJson), Schema);
        Assert.ThrowsAny<Exception>(() => JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(
            new GitWorkspaceSyncRequest(workspace, 1, "checkpoint", "key") { PushedCommitSha = "not-a-commit" }, WebJson), Schema));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("cccccccccccccccccccccccccccccccccccccccc")]
    public void Sdk_inspect_and_publish_requests_pass_gateway_schema_with_and_without_a_pushed_commit(string? pushed)
    {
        // The SDK serializes an unset pushed commit as null, so the schema must accept the field for every agent.
        var tools = new McpToolCatalog([]).List(new HashSet<string> { GitWorkspaceCapabilities.Inspect, GitWorkspaceCapabilities.Publish });
        var inspect = tools.Single(tool => tool.Capability == GitWorkspaceCapabilities.Inspect).InputSchema;
        var publish = tools.Single(tool => tool.Capability == GitWorkspaceCapabilities.Publish).InputSchema;
        JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(
            new InspectGitWorkspaceRequest(Guid.NewGuid(), 1) { PushedCommitSha = pushed }, WebJson), inspect);
        JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(
            new PublishGitWorkspaceRequest(Guid.NewGuid(), 1, "Implement ticket", "Ticket", "Evidence", "event:publish",
                [new GitValidationResult("npm test", true, 0)]) { PushedCommitSha = pushed }, WebJson), publish);
    }

    private static GitWorkspaceSyncRequest Upload(string commandClass) => new(Guid.NewGuid(), 1, "push", "workspace:checkpoint", [1, 2, 3])
    {
        CommandClass = commandClass,
        InputFingerprint = new string('a', 64),
        ResumeSummary = new(new string('b', 64), [new("npm test", true, 0), new("npm run build", false, 1)])
    };
}
