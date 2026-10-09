using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Communications;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuggestRequest = CSweet.Agent.SDK.SuggestUserActionRequest;

namespace CSweet.UnitTests;

public sealed class ApprovalSuggestionGatewayTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task NaomisTypedApprovalRequestCreatesOneCardThroughGatewayAndPreservesPendingApproval()
    {
        await using var db = ProjectApprovalReviewTests.Database();
        var (owner, agent, proposal, message) = await ProjectApprovalReviewTests.Seed(db);
        var session = await Session(db, owner.OrganizationId, agent.AgentInstallationId!.Value);
        var arguments = JsonSerializer.SerializeToElement(new SuggestRequest(message.Id, null,
            SuggestedUserActionWorkflows.ReviewApproval, "Review project setup", "The plan is ready.",
            JsonSerializer.SerializeToElement(new { approvalId = proposal.Id }), "workstream-approval-card"), JsonOptions);
        var audit = new TestAuditEventWriter();
        var first = await Call(db, session, arguments, audit);
        var replay = await Call(db, session, arguments, audit);
        Assert.False(first.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.False(replay.GetProperty("result").GetProperty("isError").GetBoolean());
        var action = Assert.Single(await db.SuggestedUserActions.ToListAsync());
        Assert.Equal(proposal.Id, ApprovalUserActionWorkflowResolver.ReadId(action.ParametersJson));
        Assert.Equal($"/organizations/{owner.OrganizationId:D}/approvals?approvalId={proposal.Id:D}", action.NavigationUri);
        Assert.Equal(2, await db.CoreConversationMessages.CountAsync());
        Assert.Null(proposal.DecidedAt);
        Assert.Equal(2, audit.Events.Count(x => x.EventType == "agent.capability.executed" && x.Outcome == "Completed"));
    }

    [Theory]
    [InlineData("approval.review.v1", "{}")]
    [InlineData("approval.review.v1", "{\"role\":\"producer\"}")]
    [InlineData("approval.review.v1", "{\"approvalId\":\"bad-id\"}")]
    [InlineData("approval.review.v1", "{\"approvalId\":\"11111111-1111-1111-1111-111111111111\",\"url\":\"https://untrusted.example\"}")]
    [InlineData("approval.review.v1", "{\"approvalId\":\"11111111-1111-1111-1111-111111111111\",\"role\":\"producer\"}")]
    [InlineData("hiring.marketplace.browse.v1", "{\"approvalId\":\"11111111-1111-1111-1111-111111111111\"}")]
    [InlineData("unknown.workflow", "{}")]
    public async Task InvalidArgumentsAreNonretryableToolErrorsWithNoDispatchOrEffects(string workflow, string parameters)
    {
        await using var db = ProjectApprovalReviewTests.Database();
        var (owner, agent, _, message) = await ProjectApprovalReviewTests.Seed(db);
        var session = await Session(db, owner.OrganizationId, agent.AgentInstallationId!.Value);
        var arguments = JsonSerializer.SerializeToElement(new { messageId = message.Id, workflowType = workflow,
            label = "Review", parameters = JsonDocument.Parse(parameters).RootElement.Clone(), idempotencyKey = "bad-request" });
        var audit = new TestAuditEventWriter();
        // No handler exists here: passing validation would dispatch and fail differently.
        var result = (await Call(db, session, arguments, audit, dispatch: false)).GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        var failure = result.GetProperty("_meta").GetProperty("csweet");
        Assert.Equal("platform.capability.validation_failed", failure.GetProperty("failureCode").GetString());
        Assert.False(failure.GetProperty("retryable").GetBoolean());
        Assert.Contains("JSON Schema validation failed", result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Empty(await db.SuggestedUserActions.ToListAsync());
        Assert.Single(await db.CoreConversationMessages.ToListAsync());
        Assert.Contains(audit.Events, x => x.EventType == "agent.capability.executed" && x.Outcome == "Failed");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApprovalCardsStillRequireProposalOwnershipAndPrivateApproverChat(bool foreignProposal)
    {
        await using var db = ProjectApprovalReviewTests.Database();
        var (owner, agent, proposal, message) = await ProjectApprovalReviewTests.Seed(db);
        var session = await Session(db, owner.OrganizationId, agent.AgentInstallationId!.Value);
        if (foreignProposal) proposal.AgentInstallationId = Guid.NewGuid();
        else message.Conversation!.IsPrivate = false;
        await db.SaveChangesAsync();
        var arguments = JsonSerializer.SerializeToElement(new { messageId = message.Id,
            workflowType = SuggestedUserActionWorkflows.ReviewApproval, label = "Review",
            parameters = new { approvalId = proposal.Id }, idempotencyKey = "denied" });
        var result = (await Call(db, session, arguments, new TestAuditEventWriter())).GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("platform.capability.denied", result.GetProperty("_meta").GetProperty("csweet").GetProperty("failureCode").GetString());
        Assert.Empty(await db.SuggestedUserActions.ToListAsync());
        Assert.Single(await db.CoreConversationMessages.ToListAsync());
    }

    [Fact]
    public void HiringSchemaRetainsItsOwnParameters()
    {
        var schema = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { SuggestedUserActionCapabilities.Suggest })).InputSchema;
        JsonSchemaValidator.ValidateSchema(schema);
        JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(new SuggestRequest(Guid.NewGuid(), null,
            SuggestedUserActionWorkflows.BrowseHiringMarketplace, "Browse producers", null,
            JsonSerializer.SerializeToElement(new { role = "producer", recommendationId = (Guid?)null }), "hiring"), JsonOptions), schema);
    }

    private static async Task<AgentSession> Session(CSweetDbContext db, Guid org, Guid installation)
    {
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), ManifestJson = "{}" };
        db.AddRange(package, new AgentInstallation { Id = installation, PackageVersionId = package.Id, BusinessId = org.ToString("D") });
        await db.SaveChangesAsync();
        return new(Guid.NewGuid().ToString("N"), "Naomi", installation.ToString("D"), org.ToString("D"),
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { SuggestedUserActionCapabilities.Suggest }, 1));
    }

    private static async Task<JsonElement> Call(CSweetDbContext db, AgentSession session, JsonElement arguments,
        TestAuditEventWriter audit, bool dispatch = true)
    {
        var service = new UserActionService(db, [new ApprovalUserActionWorkflowResolver(db)]);
        IPlatformCapabilityHandler[] handlers = dispatch ? [new CommunicationHubCapabilityHandler(db, null!, userActions: service)] : [];
        var catalog = new McpToolCatalog(handlers);
        var tool = Assert.Single(catalog.List(session.Grant.RequiredCapabilities));
        var root = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool.Name, arguments } });
        var response = await McpGatewayEndpoints.CallToolAsync(JsonSerializer.SerializeToElement(1), root, session, catalog,
            db, new PlatformCapabilityDispatcher(handlers), null!, null!, audit, 65536, NullLogger.Instance, CancellationToken.None);
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await response.ExecuteAsync(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body);
        return json.RootElement.Clone();
    }
}
