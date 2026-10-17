using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Core;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class AgentHireOperationStateTests
{
    [Theory]
    [InlineData(400, "invalid_hire", AgentHireOperationStatuses.Failed)]
    [InlineData(403, "owner_required", AgentHireOperationStatuses.Failed)]
    [InlineData(409, "installation_rejected", AgentHireOperationStatuses.Failed)]
    [InlineData(409, "approval_invalidated", AgentHireOperationStatuses.Failed)]
    [InlineData(409, "agent_source_unavailable", AgentHireOperationStatuses.Failed)]
    [InlineData(409, "agent_build_pending", AgentHireOperationStatuses.Failed)]
    [InlineData(409, "proxy_error", AgentHireOperationState.UnconfirmedStatus)]
    [InlineData(503, "installation_rejected", AgentHireOperationState.UnconfirmedStatus)]
    [InlineData(408, "timeout", AgentHireOperationState.UnconfirmedStatus)]
    public async Task ConfirmationDistinguishesExplicitRejectionsFromUncertainResults(
        int status, string error, string expectedStatus)
    {
        var workflowId = Guid.NewGuid();
        var recommendationId = Guid.NewGuid();
        const string reason = "This agent already has a different approved version.";
        using var http = new HttpClient(new ConfirmationHandler((_, _) => Task.FromResult(
            new HttpResponseMessage((HttpStatusCode)status) { Content = JsonContent.Create(new { error, message = reason }) })))
            { BaseAddress = new Uri("http://localhost/") };
        await using var realtime = new AppRealtimeState(http);
        using var state = new AgentHireOperationState(http, realtime);
        var observed = Observe(state, workflowId, expectedStatus);

        state.Start(Guid.NewGuid(), workflowId, "Gabriel Reyes", "Gabriel Reyes",
            new ConfirmHiringWorkflowRequest("confirm-rejected"), recommendationId);
        await observed.WaitAsync(TimeSpan.FromSeconds(5));

        var operation = Assert.Single(state.Operations);
        Assert.Equal(recommendationId, operation.RecommendationId);
        Assert.Equal(reason, operation.Error);
        if (expectedStatus == AgentHireOperationStatuses.Failed)
        {
            Assert.Equal("Hire rejected", operation.Phase);
            Assert.Equal(reason, operation.Detail);
            await state.RefreshAsync();
            Assert.Equal(operation, Assert.Single(state.Operations));
            await state.DismissAsync(operation);
            Assert.Empty(state.Operations);
        }
        else Assert.Contains("unknown", operation.Detail);
    }

    [Fact]
    public async Task ExplicitRejectionRemainsFailedWhenStatusReadIsUnavailableAndCanRetryTheSameReview()
    {
        var organizationId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var request = new ConfirmHiringWorkflowRequest("retry-same-review");
        var posts = 0;
        var completed = new AgentHireOperationResponse(Guid.NewGuid(), workflowId, organizationId, null,
            "Gabriel Reyes", "Gabriel Reyes", AgentHireOperationStatuses.Succeeded, "Hire complete", "Gabriel Reyes joined the team.",
            0, 0, Guid.NewGuid(), Guid.NewGuid(), false, null, null, DateTimeOffset.UtcNow);
        var handler = new ConfirmationHandler(async (message, token) =>
        {
            Assert.EndsWith($"/{workflowId:D}/confirm", message.RequestUri!.AbsolutePath);
            var replay = await message.Content!.ReadFromJsonAsync<ConfirmHiringWorkflowRequest>(token);
            Assert.Equal(request.IdempotencyKey, replay!.IdempotencyKey);
            Assert.Empty(replay.ConfigurationSettings);
            return ++posts == 1
                ? new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { error = "installation_rejected", message = "Update the agent definition." }) }
                : new(HttpStatusCode.OK) { Content = JsonContent.Create(completed) };
        }) { StatusReadUnavailable = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var realtime = new AppRealtimeState(http);
        using var state = new AgentHireOperationState(http, realtime);
        var rejected = Observe(state, workflowId, AgentHireOperationStatuses.Failed);
        state.Start(organizationId, workflowId, "Gabriel Reyes", "Gabriel Reyes", request);
        await rejected.WaitAsync(TimeSpan.FromSeconds(5));
        var operation = Assert.Single(state.Operations);
        handler.StatusReadUnavailable = false;

        await state.RetryAsync(operation);

        Assert.Equal(2, posts);
        Assert.Equal(completed, Assert.Single(state.Operations));
    }

    [Fact]
    public async Task RejectionDoesNotOverrideAuthoritativeServerSuccess()
    {
        var organizationId = Guid.NewGuid(); var workflowId = Guid.NewGuid();
        var completed = new AgentHireOperationResponse(Guid.NewGuid(), workflowId, organizationId, null,
            "Gabriel Reyes", "Gabriel Reyes", AgentHireOperationStatuses.Succeeded, "Hire complete", "Gabriel Reyes joined the team.",
            0, 0, Guid.NewGuid(), Guid.NewGuid(), false, null, null, DateTimeOffset.UtcNow);
        var handler = new ConfirmationHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            { Content = JsonContent.Create(new { error = "installation_rejected", message = "Another confirmation completed." }) }))
            { Operations = [completed] };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var realtime = new AppRealtimeState(http);
        using var state = new AgentHireOperationState(http, realtime);
        var observed = Observe(state, workflowId, AgentHireOperationStatuses.Succeeded);
        state.Start(organizationId, workflowId, "Gabriel Reyes", "Gabriel Reyes", new("concurrent-confirm"));
        await observed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(completed, Assert.Single(state.Operations));
    }

    private static Task Observe(AgentHireOperationState state, Guid workflowId, string status)
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Changed += () =>
        {
            if (state.Operations.Any(x => x.WorkflowId == workflowId && x.Status == status)) observed.TrySetResult();
        };
        return observed.Task;
    }

    private sealed class ConfirmationHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> confirm) : HttpMessageHandler
    {
        public bool StatusReadUnavailable { get; set; }
        public AgentHireOperationResponse[] Operations { get; init; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            request.Method == HttpMethod.Post ? confirm(request, token)
            : Task.FromResult(StatusReadUnavailable ? new(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Operations) });
    }

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
        var recommendationId = Guid.NewGuid();
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
            new ConfirmHiringWorkflowRequest("confirm-unknown"), recommendationId);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentHireOperationState.UnconfirmedStatus, Assert.Single(state.Operations).Status);
        Assert.Equal(recommendationId, Assert.Single(state.Operations).RecommendationId);
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
