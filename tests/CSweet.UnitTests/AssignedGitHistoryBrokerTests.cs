using System.Net;
using System.Net.Http.Json;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.SourceControl;
using CSweet.TrustedServices;

namespace CSweet.UnitTests;

public sealed class AssignedGitHistoryBrokerTests
{
    [Fact]
    public async Task Broker_forwards_only_the_scoped_history_request_and_preserves_typed_identity_and_paging()
    {
        var request = new TrustedWorkspaceOperationRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "opaque",
            Guid.NewGuid(), 7, "inspect:1") { IncludeHistory = true, HistoryOffset = 20, HistoryLimit = 5 };
        using var transport = new HistoryTransport(request);
        using var http = new HttpClient(transport) { BaseAddress = new Uri("http://core/") };
        var observed = await new CoreWorkspaceBrokerClient(http).InspectAsync(request, default);
        Assert.Equal(request.WorkspaceId, observed.WorkspaceId); Assert.False(observed.HasTrackedChanges);
        Assert.Equal(request.RepositoryId, observed.History!.RepositoryId);
        Assert.Equal(new string('a', 40), observed.History.AssignedCommitSha);
        Assert.Equal(new string('b', 40), observed.History.PublicationCommitSha);
        Assert.Equal("work/assigned", observed.History.PublicationBranch); Assert.Equal(25, observed.History.NextOffset);
        Assert.Equal("feat(task): assigned", Assert.Single(observed.History.Commits).Subject);
        Assert.Equal(new string('c', 40), Assert.Single(observed.History.Commits[0].ParentShas));
    }

    private sealed class HistoryTransport(TrustedWorkspaceOperationRequest expected) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/agent-broker/v2/workspaces/operate", request.RequestUri!.AbsolutePath);
            var operation = (await request.Content!.ReadFromJsonAsync<AgentBrokerWorkspaceOperationRequest>(token))!;
            Assert.Equal(expected.OrganizationId, operation.OrganizationId); Assert.Equal(expected.RepositoryId, operation.RepositoryId);
            Assert.Equal(expected.WorkspaceId, operation.WorkspaceId); Assert.Equal(expected.WorkItemId, operation.WorkItemId);
            Assert.Equal(7, operation.AssignmentRevision); Assert.Equal("opaque", operation.WorkspaceKey);
            Assert.Equal("inspect", operation.Operation); Assert.True(operation.IncludeHistory);
            Assert.Equal(20, operation.HistoryOffset); Assert.Equal(5, operation.HistoryLimit); Assert.Null(operation.CommitMessage);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new AgentBrokerWorkspaceOperationResult("Clean", new('a', 40), [], "") {
                History = new GitHistoryPage(expected.RepositoryId, new('a', 40), new('b', 40), new('c', 40), "work/assigned", "main",
                    new('c', 40), true, [new(new('a', 40), [new string('c', 40)], "feat(task): assigned", false)], 25) }) };
        }
    }
}
