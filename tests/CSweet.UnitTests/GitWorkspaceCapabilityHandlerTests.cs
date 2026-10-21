using CSweet.AgentHost.Broker;
using CSweet.Agent.SDK;
using System.Text.Json;

namespace CSweet.UnitTests;

public sealed class GitWorkspaceCapabilityHandlerTests
{
    [Fact]
    public async Task Assigned_history_requires_the_inspection_grant_before_database_or_host_access()
    {
        var handler = new GitWorkspaceCapabilityHandler(null!, new UnavailableTrustedGitHostClient(), null!, null!, WorkspaceSyncTestOptions.Value);
        var session = new AgentSession("session", "qa", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), 1));
        var request = new RequestCapability { RequestId = "history", Capability = GitWorkspaceCapabilities.Inspect,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new InspectGitWorkspaceRequest(Guid.NewGuid(), 1) { IncludeHistory = true })) };
        var results = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
        Assert.False(Assert.Single(results).Succeeded);
    }

    [Fact]
    public async Task ReferenceOnlyDiffReadStillRequiresTheMergeReviewGrant()
    {
        var handler = new GitWorkspaceCapabilityHandler(null!, new UnavailableTrustedGitHostClient(), null!, null!, WorkspaceSyncTestOptions.Value);
        var session = new AgentSession("session", "agent", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), 1));
        var request = new RequestCapability { RequestId = "read", Capability = GitMergeCapabilities.Review,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new ReviewGitMergeRequest(Guid.NewGuid(), 1, "reference")
                { IncludePatch = false })) };
        var results = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
        Assert.False(Assert.Single(results).Succeeded);
    }

    [Fact]
    public void DiffPagesAreBoundedAndRejectChangedPublicationSourceTargetOrPatch()
    {
        var publication = Guid.NewGuid();
        var read = new GitReviewDiffRead(publication, "source", "target", "digest", 0, 3);
        var first = GitWorkspaceCapabilityHandler.ReadReviewDiffPage(read, publication, "source", "target", "digest", "abcdef");
        Assert.Equal("abc", first.Text);
        Assert.Equal(3, first.NextOffset);
        var last = GitWorkspaceCapabilityHandler.ReadReviewDiffPage(read with { Offset = 3 }, publication, "source", "target", "digest", "abcdef");
        Assert.Equal("def", last.Text);
        Assert.Null(last.NextOffset);
        Assert.Throws<InvalidOperationException>(() => GitWorkspaceCapabilityHandler.ReadReviewDiffPage(read with { PublicationId = Guid.NewGuid() }, publication, "source", "target", "digest", "abcdef"));
        Assert.Throws<InvalidOperationException>(() => GitWorkspaceCapabilityHandler.ReadReviewDiffPage(read with { CandidateCommitSha = "changed" }, publication, "source", "target", "digest", "abcdef"));
        Assert.Throws<InvalidOperationException>(() => GitWorkspaceCapabilityHandler.ReadReviewDiffPage(read with { TargetCommitSha = "changed" }, publication, "source", "target", "digest", "abcdef"));
        Assert.Throws<InvalidOperationException>(() => GitWorkspaceCapabilityHandler.ReadReviewDiffPage(read with { PatchDigest = "changed" }, publication, "source", "target", "digest", "abcdef"));
        Assert.Throws<ArgumentException>(() => GitWorkspaceCapabilityHandler.ReadReviewDiffPage(read with { MaximumCharacters = 12001 }, publication, "source", "target", "digest", "abcdef"));
        Assert.Throws<ArgumentException>(() => GitWorkspaceCapabilityHandler.ReadReviewDiffPage(read with { Offset = -1 }, publication, "source", "target", "digest", "abcdef"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" file.txt | 2 ++")]
    [InlineData("Candidate abc for team 123.")]
    public void ReviewRejectsMissingOrStatisticsOnlyEvidence(string? patch) =>
        Assert.Throws<InvalidOperationException>(() => GitWorkspaceCapabilityHandler.RequireReviewPatch(patch));

    [Fact]
    public void ReviewPreservesTrustedPatch()
    {
        const string patch = "Review base: abc\ndiff --git a/file b/file\n+implementation";
        Assert.Equal(patch, GitWorkspaceCapabilityHandler.RequireReviewPatch(patch));
    }

    [Theory]
    [InlineData("[{\"command\":\"npm test\",\"succeeded\":true,\"exitCode\":0}]")]
    [InlineData("{\"passed\":true,\"sourceCommitSha\":\"candidate\",\"validations\":[{\"command\":\"npm test\",\"succeeded\":true,\"exitCode\":0}]}")]
    [InlineData("{\"Verdict\":\"Passed\",\"Validations\":[{\"Command\":\"npm test\",\"Succeeded\":true,\"ExitCode\":0}]}")]
    public void MergeReviewReadsLegacyAndOrchestratedQaEvidence(string json)
    {
        Assert.Equal("npm test", Assert.Single(GitWorkspaceCapabilityHandler.ReadPassingQualityEvidence(json)).Command);
    }

    [Theory]
    [InlineData("{\"passed\":false,\"validations\":[{\"command\":\"npm test\",\"succeeded\":true,\"exitCode\":0}]}")]
    [InlineData("{\"verdict\":\"Failed\",\"validations\":[{\"command\":\"npm test\",\"succeeded\":true,\"exitCode\":0}]}")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"validations\":[{\"command\":\"npm test\",\"succeeded\":false,\"exitCode\":1}]}")]
    [InlineData("{\"validations\":[{\"command\":\"npm test\",\"succeeded\":true,\"exitCode\":1}]}")]
    public void MergeReviewRejectsMissingOrFailedCommandEvidence(string json)
    {
        Assert.Throws<InvalidOperationException>(() => GitWorkspaceCapabilityHandler.ReadPassingQualityEvidence(json));
    }

    [Fact]
    public void FinalizedDeliveryAndLegacyBriefResolveTheSameRepository()
    {
        var repository = Guid.NewGuid();
        var delivery = JsonSerializer.Serialize(new CSweet.WorkManagement.Contracts.WorkItemDeliverySpecification(repository, ["move"], ["player moves"])
            { BaseBranch = "main" });
        var legacy = JsonSerializer.Serialize(new CSweet.WorkManagement.Contracts.SoftwareDevelopmentBrief(repository, "polyglot", ["move"], ["player moves"]));
        Assert.Equal(repository, GitWorkspaceCapabilityHandler.ResolveDeliveryRepository(null, delivery));
        Assert.Equal(repository, GitWorkspaceCapabilityHandler.ResolveDeliveryRepository(legacy, null));
        Assert.Equal(repository, GitWorkspaceCapabilityHandler.ResolveDeliveryRepository(legacy, delivery));
        var conflicting = JsonSerializer.Serialize(new CSweet.WorkManagement.Contracts.SoftwareDevelopmentBrief(Guid.NewGuid(), "polyglot", [], []));
        Assert.Throws<InvalidOperationException>(() => GitWorkspaceCapabilityHandler.ResolveDeliveryRepository(conflicting, delivery));
        Assert.Throws<InvalidOperationException>(() => GitWorkspaceCapabilityHandler.ResolveDeliveryRepository(null, null));
    }

    [Theory]
    [InlineData(GitWorkspaceCapabilities.ListLocks)]
    [InlineData(GitWorkspaceCapabilities.LockFile)]
    [InlineData(GitWorkspaceCapabilities.UnlockFile)]
    public async Task FileLocksRequireAnExplicitCapabilityBeforeDatabaseOrHostAccess(string capability)
    {
        var handler = new GitWorkspaceCapabilityHandler(null!, new UnavailableTrustedGitHostClient(), null!, null!, WorkspaceSyncTestOptions.Value);
        var session = new AgentSession("session", "developer", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { GitWorkspaceCapabilities.Publish }, 1));
        var request = new RequestCapability { RequestId = "lock", Capability = capability,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new { })) };
        var results = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
        Assert.False(Assert.Single(results).Succeeded);
    }

    [Fact]
    public async Task UnconfiguredGitHostFailsClosedWithoutLocalFallback()
    {
        ITrustedGitHostClient client = new UnavailableTrustedGitHostClient();
        var request = new TrustedWorkspacePrepareRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "csweet/ticket",
            null,
            "prepare-1");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PrepareAsync(request, CancellationToken.None));

        Assert.Contains("blocked", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("without exposing credentials", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
