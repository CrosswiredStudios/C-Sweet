using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CSweet.UnitTests;

public sealed partial class ConnectorStandingPolicyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task OwnerPolicyIsIdempotentAndReservesAnExactPlanWithoutExecutingIt()
    {
        await using var f = await Fixture.Create();
        var request = await f.Request();
        var policy = await f.Service.ApproveAsync(f.Org, f.Requester, f.UserId, request, default);
        Assert.Equal(policy.Id, (await f.Service.ApproveAsync(f.Org, f.Requester, f.UserId, request, default)).Id);
        var receipt = await f.Reserve(f.Plan); Assert.NotNull(receipt);
        Assert.Equal(receipt, await f.Reserve(f.Plan));
        await f.Service.RequireAuthorizationAsync(f.Org, f.Requester, f.Plan.Id, f.Plan.PlanHash, receipt, default);
        Assert.Equal("Prepared", f.Plan.Status); Assert.Null(f.Plan.ApprovalId);
        Assert.Empty(f.Inner.Db.ActionProposals);
        Assert.Single(await f.Inner.Db.PluginOperationalStates.Where(x => x.Kind == ConnectorStandingPolicyService.AuthorizationKind).ToArrayAsync());
        Assert.DoesNotContain("first content", Assert.Single(f.Audit.Metadata));
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("agent")]
    [InlineData("inactive")]
    [InlineData("foreign")]
    public async Task OnlyAnAuthenticatedHumanOwnerCanReviewOrApprove(string change)
    {
        await using var f = await Fixture.Create(); var request = await f.Request();
        if (change == "manager") f.Owner.PermissionLevel = OrganizationPermissionLevel.Manager;
        if (change == "agent") f.Owner.EmployeeType = EmployeeType.Agent;
        if (change == "inactive") f.Owner.IsActive = false;
        if (change == "foreign") f.Owner.OrganizationId = Guid.NewGuid();
        await f.Inner.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReviewAsync(f.Org, f.Requester, f.UserId, f.Plan.Id, f.Plan.PlanHash, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ApproveAsync(f.Org, f.Requester, f.UserId, request, default));
        Assert.Empty(f.Inner.Db.PluginOperationalStates);
    }

    [Fact]
    public async Task ChangedReviewAndUnspecifiedFieldsCannotBecomePermission()
    {
        await using var f = await Fixture.Create(); var request = await f.Request();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ApproveAsync(f.Org, f.Requester, f.UserId,
            request with { ReviewHash = new string('a', 64) }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.ApproveAsync(f.Org, f.Requester, f.UserId,
            request with { Definition = request.Definition with { Fields = [] } }, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReviewAsync(Guid.NewGuid(), f.Requester, f.UserId,
            f.Plan.Id, f.Plan.PlanHash, default));
        Assert.Empty(f.Inner.Db.PluginOperationalStates);
    }

    [Fact]
    public async Task SlidingRateLimitAndPolicyRevisionsCannotResetUsage()
    {
        await using var f = await Fixture.Create(); var request = await f.Request(limit: 1);
        var policy = await f.Service.ApproveAsync(f.Org, f.Requester, f.UserId, request, default);
        var first = await f.Reserve(f.Plan); Assert.NotNull(first);
        var next = await f.Prepare("next", "second content");
        Assert.Null(await f.Reserve(next));
        var replacement = await f.Service.ApproveAsync(f.Org, f.Requester, f.UserId,
            request with { ExpectedRevision = policy.Revision }, default);
        Assert.NotEqual(policy.Id, replacement.Id);
        Assert.Null(await f.Reserve(next));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RequireAuthorizationAsync(f.Org, f.Requester,
            f.Plan.Id, f.Plan.PlanHash, first, default));
        f.Clock.Advance(TimeSpan.FromMinutes(59)); Assert.Null(await f.Reserve(next));
        f.Clock.Advance(TimeSpan.FromMinutes(2)); Assert.NotNull(await f.Reserve(next));
        // Old plan receipts can never be silently rebound to a newer policy revision.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Reserve(f.Plan));
    }

    [Fact]
    public async Task RevocationFromAnotherContextIsSeenByTheOriginalScopedService()
    {
        await using var f = await Fixture.Create(); var policy = await f.Approve();
        var authorization = (await f.Reserve(f.Plan))!;
        var options = (DbContextOptions<CSweetDbContext>)f.Inner.Db.GetService<IDbContextOptions>();
        await using var other = new CSweetDbContext(options);
        var service = new ConnectorStandingPolicyService(other, new(other), f.Audit, f.Clock);
        await service.RevokeAsync(f.Org, f.Requester, f.UserId, f.Plan.Capability, policy.Id, policy.Revision, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RequireAuthorizationAsync(f.Org, f.Requester,
            f.Plan.Id, f.Plan.PlanHash, authorization, default));
        Assert.Null(await f.Reserve(f.Plan));
        // Retrying a previous owner submission reports its revoked record, never restores it.
        Assert.Equal("Revoked", (await f.Service.ApproveAsync(f.Org, f.Requester, f.UserId, f.LastRequest!, default)).Status);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("mode")]
    [InlineData("expired")]
    [InlineData("binding")]
    [InlineData("profile")]
    [InlineData("connection")]
    [InlineData("requester-package")]
    public async Task EveryReservationIsRecheckedAgainstCurrentAuthority(string change)
    {
        await using var f = await Fixture.Create(); await f.Approve();
        var authorization = (await f.Reserve(f.Plan))!;
        switch (change)
        {
            case "owner": f.Owner.IsActive = false; break;
            case "mode": (await f.Inner.Db.AgentInstallationConfigurations.SingleAsync()).SettingsJson = "{}"; break;
            case "expired": f.Clock.Advance(TimeSpan.FromDays(3)); break;
            case "binding": (await f.Inner.Db.AgentCapabilityBindings.SingleAsync()).ApprovedAt = f.Clock.GetUtcNow().AddMinutes(1); break;
            case "profile": (await f.Inner.Db.ConnectorProfileApprovals.SingleAsync()).ApprovedAt = f.Clock.GetUtcNow().AddMinutes(1); break;
            case "connection": f.Inner.Connection.UpdatedAt = f.Clock.GetUtcNow().AddMinutes(1); break;
            case "requester-package": f.Inner.Requester.PackageVersion!.PackageDigest = new string('c', 64); break;
        }
        await f.Inner.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RequireAuthorizationAsync(f.Org, f.Requester,
            f.Plan.Id, f.Plan.PlanHash, authorization, default));
        Assert.Null(await f.Reserve(f.Plan));
    }

    [Fact]
    public async Task RepeatedAccountSelectionPreservesPolicyButReviewedBuildChangesRevokeIt()
    {
        await using var f = await Fixture.Create(); await f.Approve();
        var binding = await f.Inner.Db.AgentCapabilityBindings.SingleAsync();
        var approvedAt = binding.ApprovedAt;
        var selection = new ConnectorBindingService(f.Inner.Db);
        await selection.BindAsync(f.Org, f.Requester, "account", f.Inner.Connector.Id, default);
        Assert.Equal(approvedAt, binding.ApprovedAt);
        Assert.NotNull(await f.Reserve(f.Plan));
        f.Inner.Connector.PackageVersion!.PackageDigest = new string('b', 64);
        (await f.Inner.Db.ConnectorProfileApprovals.SingleAsync()).PackageDigest = f.Inner.Connector.PackageVersion.PackageDigest;
        await f.Inner.Db.SaveChangesAsync();
        await selection.BindAsync(f.Org, f.Requester, "account", f.Inner.Connector.Id, default);
        Assert.Equal("Revoked", (await f.Service.GetAsync(f.Org, f.Requester, f.UserId, f.Plan.Capability, default))!.Status);
    }

    [Fact]
    public async Task LifecycleRevocationIsTenantScopedAndCannotBeReversedByRepeatingConsent()
    {
        await using var f = await Fixture.Create(); await f.Approve();
        await ConnectorStandingPolicyService.RevokeForConsumersAsync(f.Inner.Db, Guid.NewGuid(), [f.Requester], default);
        await ConnectorStandingPolicyService.RevokeForConsumersAsync(f.Inner.Db, f.Org, [Guid.NewGuid()], default);
        await f.Inner.Db.SaveChangesAsync(); Assert.NotNull(await f.Reserve(f.Plan));
        await ConnectorStandingPolicyService.RevokeForConsumersAsync(f.Inner.Db, f.Org, [f.Requester], default);
        await f.Inner.Db.SaveChangesAsync(); Assert.Null(await f.Reserve(f.Plan));
        var record = await f.Inner.Db.PluginOperationalStates.SingleAsync(x => x.Kind == ConnectorStandingPolicyService.PolicyKind);
        var revision = record.Revision;
        await ConnectorStandingPolicyService.RevokeForConsumersAsync(f.Inner.Db, f.Org, [f.Requester], default);
        await f.Inner.Db.SaveChangesAsync(); Assert.Equal(revision, record.Revision);
        Assert.Equal("Revoked", (await f.Service.GetAsync(f.Org, f.Requester, f.UserId, f.Plan.Capability, default))!.Status);
    }

    [Fact]
    public async Task ReceiptAndPolicyTamperingFailClosed()
    {
        await using var f = await Fixture.Create(); await f.Approve();
        var authorization = (await f.Reserve(f.Plan))!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RequireAuthorizationAsync(f.Org, f.Requester,
            f.Plan.Id, f.Plan.PlanHash, authorization with { PlanId = Guid.NewGuid() }, default));
        var record = await f.Inner.Db.PluginOperationalStates.SingleAsync(x => x.Kind == ConnectorStandingPolicyService.PolicyKind);
        var policy = JsonSerializer.Deserialize<ConnectorStandingPolicyService.StoredPolicy>(record.PayloadJson, Json)!;
        record.PayloadJson = JsonSerializer.Serialize(policy with { Definition = policy.Definition with { MaximumActionsPerHour = 1000 } }, Json);
        await f.Inner.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Reserve(f.Plan));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("destructive")]
    [InlineData("irreversible")]
    [InlineData("live")]
    [InlineData("security-sensitive-write")]
    [InlineData("fiscal-write")]
    [InlineData("unknown")]
    public async Task EffectMetadataNotApplicationActionNamesControlsHardGates(string effect)
    {
        await using var f = await Fixture.Create();
        Assert.False(ConnectorStandingPolicyRules.CanAuthorize(f.Operation with { Effect = effect }));
        Assert.Throws<ArgumentException>(() => ConnectorStandingPolicyRules.Validate(f.Definition(), f.Operation with { Effect = effect }, f.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task RulesCompareTypedValuesDecodedTextSchedulesAndMediaWithoutProviderVocabulary()
    {
        await using var f = await Fixture.Create();
        var plan = await f.Inner.Service.RevalidateAsync(f.Org, f.Requester, f.Plan.Id, f.Plan.PlanHash, default);
        var rules = f.Definition();
        Assert.True(ConnectorStandingPolicyRules.Matches(rules, plan, f.Clock.GetUtcNow()));
        var title = new ConnectorPolicyField("body", "/title");
        var constrained = rules with { Fields = rules.Fields.Select(x => x.Field == title
            ? x with { AllowAny = false, AllowedValues = [JsonSerializer.SerializeToElement("approved only")] } : x).ToArray() };
        Assert.False(ConnectorStandingPolicyRules.Matches(constrained, plan, f.Clock.GetUtcNow()));
        Assert.False(ConnectorStandingPolicyRules.Matches(rules with { EscalationTerms = ["CONTENT"] }, plan, f.Clock.GetUtcNow()));
        var escaped = plan with { Request = plan.Request with { Body = "{\"title\":\"l\\u0065gal\"}" } };
        Assert.False(ConnectorStandingPolicyRules.Matches(rules with { EscalationTerms = ["legal"] }, escaped, f.Clock.GetUtcNow()));
        Assert.False(ConnectorStandingPolicyRules.Matches(rules with { ScheduledAt = title }, plan, f.Clock.GetUtcNow()));
        var media = plan with { Media = new(Guid.NewGuid(), new string('a', 64), 100, "video/example") };
        Assert.False(ConnectorStandingPolicyRules.Matches(rules, media, f.Clock.GetUtcNow()));
        Assert.True(ConnectorStandingPolicyRules.Matches(rules with { MaximumMediaBytes = 100, AllowedMediaTypes = ["video/example"] }, media, f.Clock.GetUtcNow()));
        Assert.False(ConnectorStandingPolicyRules.Matches(rules, plan with { Request = plan.Request with { Method = "DELETE" } }, f.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task DelayedApprovalsRecheckExecutionTimeQuotaAndLongTransfersKeepTheirStartPermit()
    {
        await using var f = await Fixture.Create();
        await f.Service.ApproveAsync(f.Org, f.Requester, f.UserId, await f.Request(limit: 1), default);
        var first = (await f.Reserve(f.Plan))!;
        f.Clock.Advance(TimeSpan.FromMinutes(61));
        var secondPlan = await f.Prepare("second", "second content");
        var second = (await f.Reserve(secondPlan))!;
        f.Plan.Status = "Approved"; secondPlan.Status = "Approved"; await f.Inner.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RequireAuthorizationAsync(f.Org, f.Requester,
            f.Plan.Id, f.Plan.PlanHash, first, default));
        await f.Service.RequireAuthorizationAsync(f.Org, f.Requester, secondPlan.Id, secondPlan.PlanHash, second, default);
        secondPlan.Status = "Executing"; await f.Inner.Db.SaveChangesAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(61));
        var third = await f.Prepare("third", "third content"); Assert.NotNull(await f.Reserve(third));
        // Revalidating chunks never charges the action again after its start left the sliding window.
        await f.Service.RequireAuthorizationAsync(f.Org, f.Requester, secondPlan.Id, secondPlan.PlanHash, second, default);
    }

    [Fact]
    public async Task OperationalStateConcurrencyRollsBackTheCompetingRateReceiptAtomically()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseSqlite(connection).Options;
        await using var first = new CSweetDbContext(options);
        await first.Database.ExecuteSqlRawAsync("""
            CREATE TABLE PluginOperationalStates (Id TEXT PRIMARY KEY, OrganizationId TEXT NOT NULL,
            AgentInstallationId TEXT NOT NULL, Kind TEXT NOT NULL, ExternalKey TEXT NOT NULL,
            PayloadJson TEXT NOT NULL, Revision INTEGER NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, AvailableAt TEXT NULL);
            CREATE UNIQUE INDEX IX_PolicyIdentity ON PluginOperationalStates (AgentInstallationId, Kind, ExternalKey);
            """);
        var row = new PluginOperationalState { Id = Guid.NewGuid(), AgentInstallationId = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(), Kind = ConnectorStandingPolicyService.PolicyKind, ExternalKey = "example.api.change.v1", Revision = 1 };
        first.Add(row); await first.SaveChangesAsync();
        await using var second = new CSweetDbContext(options);
        var stale = await second.PluginOperationalStates.SingleAsync();
        row.Revision++; row.PayloadJson = "{\"reservations\":[1]}";
        first.Add(new PluginOperationalState { Id = Guid.NewGuid(), AgentInstallationId = row.AgentInstallationId,
            OrganizationId = row.OrganizationId, Kind = ConnectorStandingPolicyService.AuthorizationKind, ExternalKey = "first", Revision = 1 });
        await first.SaveChangesAsync();
        stale.Revision++; stale.PayloadJson = "{\"reservations\":[2]}";
        second.Add(new PluginOperationalState { Id = Guid.NewGuid(), AgentInstallationId = row.AgentInstallationId,
            OrganizationId = row.OrganizationId, Kind = ConnectorStandingPolicyService.AuthorizationKind, ExternalKey = "second", Revision = 1 });
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        await using var verify = new CSweetDbContext(options);
        Assert.Equal("first", (await verify.PluginOperationalStates.SingleAsync(x => x.Kind == ConnectorStandingPolicyService.AuthorizationKind)).ExternalKey);
        Assert.Equal("{\"reservations\":[1]}", (await verify.PluginOperationalStates.SingleAsync(x => x.Kind == ConnectorStandingPolicyService.PolicyKind)).PayloadJson);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
    private sealed class Audit : IAuditEventWriter
    {
        public List<string> Metadata { get; } = [];
        public Task WriteAsync(string type, string entity, Guid? id, string? summary, string? metadata = null, CancellationToken cancellationToken = default)
        { Metadata.Add(metadata ?? ""); return Task.CompletedTask; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public ConnectorPlanServiceTests.Fixture Inner { get; private init; } = null!;
        public Guid Org => Inner.Organization;
        public Guid Requester => Inner.Requester.Id;
        public Guid UserId { get; } = Guid.NewGuid();
        public OrganizationUser Owner { get; private set; } = null!;
        public ConnectorExecution Plan { get; private set; } = null!;
        public PluginProviderOperationDeclaration Operation { get; private set; } = null!;
        public Clock Clock { get; } = new();
        public Audit Audit { get; } = new();
        public ConnectorStandingPolicyService Service => new(Inner.Db, Inner.Service, Audit, Clock);
        public ApproveConnectorStandingPolicyRequest? LastRequest;
        public Task<ConnectorStandingPolicyService.Authorization?> Reserve(ConnectorExecution plan) => Service.TryReserveAsync(Org, Requester, plan.Id, plan.PlanHash, default);
        public Task<ConnectorExecution> Prepare(string key, string content) => Inner.Service.PrepareAsync(Org, Requester,
            ConnectorPlanServiceTests.Fixture.Capability, ConnectorPlanServiceTests.Fixture.Input(content), key, default);
        public async Task<ApproveConnectorStandingPolicyRequest> Request(int limit = 5)
        {
            var review = await Service.ReviewAsync(Org, Requester, UserId, Plan.Id, Plan.PlanHash, default);
            return LastRequest = new(Plan.Id, Plan.PlanHash, review.ReviewHash, Definition(limit), null);
        }
        public async Task<ConnectorStandingPolicyView> Approve() => await Service.ApproveAsync(Org, Requester, UserId, await Request(), default);
        public ConnectorStandingPolicyDefinition Definition(int limit = 5) => new(
            ConnectorStandingPolicyRules.Fields(Operation).Select(x => new ConnectorPolicyFieldRule(x, true, [])).ToArray(),
            [0, 1, 2, 3, 4, 5, 6], 0, 1440, limit, Clock.GetUtcNow().AddMinutes(-1), Clock.GetUtcNow().AddDays(2), []);
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
        public static async Task<Fixture> Create()
        {
            var f = new Fixture { Inner = await ConnectorPlanServiceTests.Fixture.Create() };
            var manifest = JsonSerializer.Deserialize<PluginManifest>(f.Inner.Connector.PackageVersion!.ManifestJson, Json)!;
            f.Operation = manifest.ProviderOperations[0] with { Effect = "write", Idempotency = "caller-key",
                Http = manifest.ProviderOperations[0].Http! with { Method = "POST", BodyInputs = new Dictionary<string, string> { ["/title"] = "/search" } } };
            manifest = manifest with { Provides = [manifest.Provides[0] with { Idempotency = "caller-key", Description = "Change an example account item." }],
                ProviderOperations = [f.Operation] };
            f.Inner.Connector.PackageVersion.ManifestJson = JsonSerializer.Serialize(manifest, Json);
            f.Owner = new() { Id = Guid.NewGuid(), OrganizationId = f.Org, ApplicationUserId = f.UserId, DisplayName = "Owner",
                EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner, IsActive = true };
            f.Inner.Db.AddRange(f.Owner, new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Org,
                AgentInstallationId = f.Requester, EmployeeType = EmployeeType.Agent, DisplayName = "Specialist", IsActive = true });
            f.Inner.Db.AgentInstallationConfigurations.Add(new() { Id = Guid.NewGuid(), AgentInstallationId = f.Requester,
                SchemaVersion = "1", SettingsJson = "{\"approvalMode\":\"Fully Autonomous\"}" });
            await f.Inner.Db.SaveChangesAsync(); f.Plan = await f.Prepare("first", "first content"); return f;
        }
    }
}
