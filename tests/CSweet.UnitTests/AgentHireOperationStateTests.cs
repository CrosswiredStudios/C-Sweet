using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Core;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class AgentHireOperationStateTests
{
    [Fact]
    public async Task FailedConfirmationResponseDoesNotOverrideCompletedServerHire()
    {
        var organizationId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var completed = new AgentHireOperationResponse(
            Guid.NewGuid(), workflowId, organizationId, null, "Evelyn Brooks", "Evelyn Brooks",
            AgentHireOperationStatuses.Succeeded, "Hire complete", "Evelyn Brooks joined the team.",
            0, 0, Guid.NewGuid(), Guid.NewGuid(), false,
            $"/organizations/{organizationId:D}/employees", null, now);
        using var http = new HttpClient(new HireResponseHandler(completed))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        await using var realtime = new AppRealtimeState(http);
        using var state = new AgentHireOperationState(http, realtime);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Changed += () =>
        {
            if (state.Operations.Any(x => x.WorkflowId == workflowId &&
                                          x.Status == AgentHireOperationStatuses.Succeeded))
                observed.TrySetResult();
        };

        state.Start(organizationId, workflowId, "Evelyn Brooks", "Evelyn Brooks",
            new ConfirmHiringWorkflowRequest("confirm-test"));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Yield();

        var operation = Assert.Single(state.Operations);
        Assert.Equal(AgentHireOperationStatuses.Succeeded, operation.Status);
        Assert.Equal(completed.Id, operation.Id);
    }

    [Fact]
    public async Task UnreadableHireResultStaysUnconfirmed()
    {
        var workflowId = Guid.NewGuid();
        using var http = new HttpClient(new UnavailableHireResponseHandler())
        {
            BaseAddress = new Uri("http://localhost/")
        };
        await using var realtime = new AppRealtimeState(http);
        using var state = new AgentHireOperationState(http, realtime);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Changed += () =>
        {
            if (state.Operations.Any(x => x.WorkflowId == workflowId &&
                                          x.Status == AgentHireOperationState.UnconfirmedStatus))
                observed.TrySetResult();
        };
        state.Start(Guid.NewGuid(), workflowId, "Evelyn Brooks", "Evelyn Brooks",
            new ConfirmHiringWorkflowRequest("confirm-unknown"));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentHireOperationState.UnconfirmedStatus, Assert.Single(state.Operations).Status);
    }

    private sealed class UnavailableHireResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class HireResponseHandler(AgentHireOperationResponse completed) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = JsonContent.Create(new { title = "Server error", status = 500 })
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[] { completed })
            });
        }
    }
}
