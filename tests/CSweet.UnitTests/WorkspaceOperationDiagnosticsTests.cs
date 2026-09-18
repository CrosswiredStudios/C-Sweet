using System.Net;
using System.Net.Http.Json;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.SourceControl;
using CSweet.Infrastructure.SourceControl;
using CSweet.TrustedServices;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class WorkspaceOperationDiagnosticsTests
{
    [Fact]
    public async Task Original_publication_error_and_recovery_survive_both_HTTP_boundaries()
    {
        var original = WorkspaceOperationErrors.Describe(new InvalidOperationException(
            "The idempotency key was already used with different content."));
        using var gitHttp = Client(new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            { Content = JsonContent.Create(original.Failure) })));
        var git = new TrustedSourceControlHostClient(gitHttp, Options.Create(new TrustedServiceAuthenticationOptions()));
        using var coreHttp = Client(new Transport(async (_, ct) =>
        {
            var error = await Assert.ThrowsAsync<WorkspaceOperationException>(() => git.ApplyInternalSnapshotAsync(
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "publish", new string('a', 40), "task", "main", "key", [], "digest", 0, 0), ct));
            var forwarded = WorkspaceOperationErrors.Describe(error);
            return new((HttpStatusCode)forwarded.Status) { Content = JsonContent.Create(forwarded.Failure) };
        }));
        var core = new CoreWorkspaceBrokerClient(coreHttp);
        var failure = await Assert.ThrowsAsync<WorkspaceOperationException>(() => core.PublishAsync(new(
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "workspace", Guid.NewGuid(), 1, "key"), "Commit", "Task", "Summary", []), default));
        Assert.Equal(original.Failure, failure.Failure);
        Assert.Contains(original.Failure.Message, failure.Message);
        Assert.Contains("new operation key", failure.Message);
        Assert.Contains(original.Failure.DiagnosticId, failure.Message);
        Assert.Equal(409, failure.StatusCode);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(503)]
    public async Task Legacy_or_proxy_errors_keep_HTTP_status_without_exposing_the_body(int status)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("<html>password=private-value</html>") };
        var error = await Assert.ThrowsAsync<WorkspaceOperationException>(() =>
            WorkspaceOperationErrors.EnsureSuccessAsync(response, "Core", "publish", default));
        Assert.Contains($"HTTP {status}", error.Message);
        Assert.DoesNotContain("private-value", error.Message);
        Assert.Contains("no structured diagnostic", error.Message);
    }

    [Fact]
    public async Task Oversized_response_is_not_forwarded()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
            { Content = new StringContent(new string('x', 20_000)) };
        var error = await Assert.ThrowsAsync<WorkspaceOperationException>(() =>
            WorkspaceOperationErrors.EnsureSuccessAsync(response, "GitHost", "publish", default));
        Assert.Equal("workspace.http_failure", error.Failure.Code);
    }

    [Fact]
    public void Error_excerpt_keeps_cause_but_removes_secrets_and_host_details()
    {
        var (failure, _) = WorkspaceOperationErrors.Describe(new IOException(
            "Disk full; password=private-value token=another-secret https://user:secret@host/path\n   at Internal.Method()\n C:\\Users\\Private\\repo"));
        Assert.Contains("Disk full", failure.Message);
        foreach (var secret in new[] { "private-value", "another-secret", "user:secret", "Internal.Method", "Private" })
            Assert.DoesNotContain(secret, failure.Message);
        Assert.True(Guid.TryParseExact(failure.DiagnosticId, "N", out _));
    }

    [Theory]
    [InlineData("Workspace assignment is stale.", "workspace.assignment_stale")]
    [InlineData("This publication was superseded by a later work-branch revision.", "workspace.publication_superseded")]
    [InlineData("The work branch changed. Prepare or refresh its exact current revision before publishing.", "workspace.branch_changed")]
    public void Different_conflicts_have_distinct_actionable_diagnostics(string message, string code)
    {
        var (failure, _) = WorkspaceOperationErrors.Describe(new InvalidOperationException(message));
        Assert.Equal(code, failure.Code);
        Assert.Equal(message, failure.Message);
        Assert.NotEmpty(failure.NextStep);
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://localhost/") };
    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
