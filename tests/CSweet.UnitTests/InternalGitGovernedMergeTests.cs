using System.Text.Json;
using CSweet.Application.SourceControl;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class InternalGitGovernedMergeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeGitRequiresRealSignedQaApprovalAndRecoversLostMergeResponse(bool changedHead)
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-governed-native", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, ".csweet-git-store"), "test");
        try
        {
            await using var db = Database(); var context = await SeedAsync(db);
            var repo = await db.SourceControlRepositories.SingleAsync(); var publication = await db.SourceControlPublications.SingleAsync();
            var qa = await db.SourceControlValidations.SingleAsync(); var lead = await db.SourceControlMergeAuthorizations.SingleAsync();
            var store = new CSweet.TrustedServices.InternalGitRepositoryStore(Microsoft.Extensions.Options.Options.Create(new CSweet.TrustedServices.InternalGitStorageOptions {
                RepositoryRoot = root, ExpectedStoreId = "test", TemporaryRoot = Path.Combine(root, "operations") }));
            var artifacts = new CSweet.TrustedServices.WorkspaceArtifactValidator();
            await store.ExecuteAsync(new(context.OrganizationId, repo.Id, "create", "main"));
            var prepared = await store.PrepareAsync(new(context.OrganizationId, repo.Id, publication.WorkspaceId, "main", "work/feature", null, "prepare"), artifacts);
            var input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
            await File.WriteAllTextAsync(Path.Combine(input, "feature.txt"), "reviewed feature");
            using var archive = new MemoryStream(); var manifest = await artifacts.CreateZipAsync(input, archive);
            var request = new InternalGitSnapshotOperation(context.OrganizationId, repo.Id, publication.WorkspaceId, "publish", prepared.BaseCommitSha,
                "work/feature", "main", "publish", archive.ToArray(), manifest.Sha256, manifest.FileCount, manifest.TotalBytes, "Feature");
            var published = await store.ApplySnapshotAsync(request, artifacts);
            publication.CommitSha = published.CommitSha!; qa.CommitSha = publication.CommitSha; qa.Status = SourceControlValidationStatus.Pending;
            lead.CommitSha = publication.CommitSha; lead.AuthorizedAt = DateTimeOffset.UtcNow;
            var signer = new CSweet.Infrastructure.SourceControl.DataProtectionSourceControlDecisionSigner(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
            var signature = signer.Sign(new(lead.OrganizationId, lead.PublicationId, lead.CommitSha, lead.AuthorizedByOrganizationUserId,
                lead.TeamPolicyRevision, lead.AuthorizedAt, lead.ExpiresAt));
            lead.DecisionSignature = signature; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            var host = new Host { NativeStore = store, LoseResponse = !changedHead }; var executor = new GovernedMergeWorkActionExecutor(db, host, signer, TimeProvider.System);
            Assert.Equal("blocked", (await executor.ExecuteAsync(context)).OutcomeCode); Assert.Equal(0, host.Calls);
            qa = await db.SourceControlValidations.AsTracking().SingleAsync(); qa.Status = SourceControlValidationStatus.Passed;
            lead = await db.SourceControlMergeAuthorizations.AsTracking().SingleAsync(); lead.DecisionSignature = "tampered";
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            Assert.Equal("blocked", (await executor.ExecuteAsync(context)).OutcomeCode); Assert.Equal(0, host.Calls);
            Assert.Equal(prepared.BaseCommitSha, (await store.ExecuteAsync(new(context.OrganizationId, repo.Id, "inspect"))).Refs.Single(r => r.Name == "refs/heads/main").Sha);
            lead = await db.SourceControlMergeAuthorizations.AsTracking().SingleAsync(); lead.DecisionSignature = signature; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            if (changedHead)
            {
                await store.ApplySnapshotAsync(request with { BaseSha = published.CommitSha!, IdempotencyKey = "unreviewed", CommitMessage = "Unreviewed revision" }, artifacts);
                Assert.Equal("blocked", (await executor.ExecuteAsync(context)).OutcomeCode);
                db.ChangeTracker.Clear();
                Assert.Equal(SourceControlPublicationStatus.Superseded, (await db.SourceControlPublications.SingleAsync()).Status);
                Assert.Equal(SourceControlValidationStatus.Superseded, (await db.SourceControlValidations.SingleAsync()).Status);
                Assert.NotNull((await db.SourceControlMergeAuthorizations.SingleAsync()).RevokedAt);
                Assert.Equal(prepared.BaseCommitSha, (await store.ExecuteAsync(new(context.OrganizationId, repo.Id, "inspect"))).Refs.Single(r => r.Name == "refs/heads/main").Sha);
            }
            else
            {
                Assert.Equal("blocked", (await executor.ExecuteAsync(context)).OutcomeCode); // Native merge finished, but the transport response was lost.
                db.ChangeTracker.Clear();
                Assert.Equal(SourceControlMergeStatus.Failed, (await db.SourceControlMergeJobs.SingleAsync()).Status);
                Assert.Equal("merged", (await executor.ExecuteAsync(context)).OutcomeCode); db.ChangeTracker.Clear();
                Assert.Equal("merged", (await executor.ExecuteAsync(context)).OutcomeCode); Assert.Equal(2, host.Calls);
                var job = await db.SourceControlMergeJobs.SingleAsync(); Assert.Equal(SourceControlMergeStatus.Merged, job.Status);
                var tree = await store.ExecuteAsync(new(context.OrganizationId, repo.Id, "inspect", Ref: "refs/heads/main", Path: "feature.txt"));
                Assert.Equal(job.MergeCommitSha, tree.Refs.Single(r => r.Name == "refs/heads/main").Sha); Assert.Equal("reviewed feature", tree.Content);
            }
        }
        finally
        {
            if (Directory.Exists(root)) { foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task ExactQaAndSignedAuthorizationMergeThroughInternalHostAndReplayWithoutSecondCall()
    {
        await using var db = Database(); var context = await SeedAsync(db);
        var host = new Host(); var executor = new GovernedMergeWorkActionExecutor(db, host, new Signer(), TimeProvider.System);
        Assert.Equal("merged", (await executor.ExecuteAsync(context)).OutcomeCode);
        Assert.Equal("merged", (await executor.ExecuteAsync(context)).OutcomeCode);
        Assert.Equal(1, host.Calls);
        Assert.Equal(new string('a', 40), host.Request!.ExpectedHeadSha);
        Assert.Equal("work/feature", host.Request.SourceBranch);
        Assert.Equal("main", host.Request.TargetBranch);
        Assert.Equal(SourceControlPublicationStatus.Merged, (await db.SourceControlPublications.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("qa")]
    [InlineData("authorization")]
    [InlineData("signature")]
    [InlineData("policy")]
    [InlineData("archive")]
    public async Task MissingOrStaleGovernanceCannotReachInternalHost(string invalid)
    {
        await using var db = Database(); var context = await SeedAsync(db);
        if (invalid == "qa") (await db.SourceControlValidations.SingleAsync()).CommitSha = new string('c', 40);
        if (invalid == "authorization") (await db.SourceControlMergeAuthorizations.SingleAsync()).ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        if (invalid == "signature") (await db.SourceControlMergeAuthorizations.SingleAsync()).DecisionSignature = "invalid";
        if (invalid == "policy") (await db.TeamRepositoryPolicies.SingleAsync()).Revision++;
        if (invalid == "archive") (await db.SourceControlRepositories.SingleAsync()).ArchivedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var host = new Host();
        var result = await new GovernedMergeWorkActionExecutor(db, host, new Signer(), TimeProvider.System).ExecuteAsync(context);
        Assert.Equal("blocked", result.OutcomeCode); Assert.Equal(0, host.Calls);
    }

    [Fact]
    public async Task AdministratorPolicyCreatesOnePendingApprovalAndNeverMergesBeforeApproval()
    {
        await using var db = Database(); var context = await SeedAsync(db);
        (await db.TeamRepositoryPolicies.SingleAsync()).MergeApprovalMode = TeamMergeApprovalMode.LeadAndAdministratorApproval;
        await db.SaveChangesAsync(); var host = new Host();
        var executor = new GovernedMergeWorkActionExecutor(db, host, new Signer(), TimeProvider.System);
        Assert.Equal("blocked", (await executor.ExecuteAsync(context)).OutcomeCode);
        Assert.Equal("blocked", (await executor.ExecuteAsync(context)).OutcomeCode);
        Assert.Single(db.SourceControlApprovals); Assert.Equal(0, host.Calls);
    }

    [Fact]
    public async Task ChangedSourceInvalidatesQaAndLeadAuthorization()
    {
        await using var db = Database(); var context = await SeedAsync(db);
        var host = new Host { HeadMatched = false };
        Assert.Equal("blocked", (await new GovernedMergeWorkActionExecutor(db, host, new Signer(), TimeProvider.System).ExecuteAsync(context)).OutcomeCode);
        Assert.Equal(SourceControlPublicationStatus.Superseded, (await db.SourceControlPublications.SingleAsync()).Status);
        Assert.Equal(SourceControlValidationStatus.Superseded, (await db.SourceControlValidations.SingleAsync()).Status);
        Assert.NotNull((await db.SourceControlMergeAuthorizations.SingleAsync()).RevokedAt);
    }

    private static CSweetDbContext Database() => new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static async Task<TrustedWorkActionContext> SeedAsync(CSweetDbContext db)
    {
        var business = Guid.NewGuid(); var team = Guid.NewGuid(); var sha = new string('a', 40);
        var connection = new SourceControlConnection { Id = Guid.NewGuid(), OrganizationId = business, Provider = SourceControlProvider.InternalGit, Status = SourceControlConnectionStatus.Connected };
        var repository = new SourceControlRepository { Id = Guid.NewGuid(), OrganizationId = business, Connection = connection, ConnectionId = connection.Id, Name = "engine", DefaultBranch = "main", IsPrivate = true, Status = SourceControlRepositoryStatus.Ready };
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = business, AssignmentRevision = 1, Title = "Feature" };
        var execution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = item.Id, WorkItem = item };
        var workspace = new SourceControlWorkspace { Id = Guid.NewGuid(), OrganizationId = business, RepositoryId = repository.Id, WorkItemId = item.Id, AssignmentRevision = 1, TeamId = team };
        var publication = new SourceControlPublication { Id = Guid.NewGuid(), OrganizationId = business, RepositoryId = repository.Id, WorkspaceId = workspace.Id, CommitSha = sha, TicketBranch = "work/feature", TargetBranch = "main" };
        var policy = new TeamRepositoryPolicy { Id = Guid.NewGuid(), OrganizationId = business, TeamId = team, RepositoryId = repository.Id, Revision = 1 };
        var validation = new SourceControlValidation { Id = Guid.NewGuid(), OrganizationId = business, PublicationId = publication.Id, CommitSha = sha, Status = SourceControlValidationStatus.Passed };
        var authorization = new SourceControlMergeAuthorization { Id = Guid.NewGuid(), OrganizationId = business, PublicationId = publication.Id, CommitSha = sha, TeamPolicyRevision = 1, DecisionSignature = "signed", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        db.AddRange(connection, repository, item, execution, workspace, publication, policy, validation, authorization);
        await db.SaveChangesAsync();
        return new(business, Guid.NewGuid(), Guid.NewGuid(), execution.Id, Guid.NewGuid(), item.Id, "DEV-1", GovernedMergeWorkActionExecutor.ActionName, JsonSerializer.SerializeToElement(new { }));
    }
    private sealed class Signer : ISourceControlDecisionSigner
    {
        public string Sign(SourceControlMergeDecision decision) => "signed";
        public bool Verify(SourceControlMergeDecision decision, string signature) => signature == "signed";
    }
    private sealed class Host : ITrustedSourceControlHostClient
    {
        public CSweet.TrustedServices.InternalGitRepositoryStore? NativeStore { get; set; }
        public bool LoseResponse { get; set; }
        public int Calls { get; private set; }
        public InternalGitMergeRequest? Request { get; private set; }
        public bool HeadMatched { get; set; } = true;
        public async Task<InternalGitMergeResult> MergeInternalAsync(InternalGitMergeRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; Request = request;
            if (NativeStore is null) return new(HeadMatched, HeadMatched, HeadMatched ? new string('b', 40) : null);
            var result = await NativeStore.MergeInternalAsync(request, cancellationToken);
            if (LoseResponse) { LoseResponse = false; throw new HttpRequestException("Simulated lost response"); }
            return result;
        }
        public Task<TrustedInstallationDescriptor> DescribeInstallationAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrustedMergeResult> MergeAsync(TrustedMergeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TrustedWorkspaceSnapshot> PrepareWorkspaceAsync(TrustedWorkspaceSnapshotRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
