using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class McpAgentSessionServiceTests
{
    [Fact]
    public async Task EstablishAndRotate_StoreOnlyHashesAndExpireTheOverlapToken()
    {
        await using var fixture = await Fixture.CreateAsync();

        var issue = await fixture.Service.EstablishAsync(
            fixture.WorkloadToken,
            fixture.Runtime.Id,
            fixture.Runtime.TickId,
            fixture.Installation.Id,
            fixture.Installation.BusinessId,
            fixture.Package.AgentId,
            fixture.Package.Version,
            CancellationToken.None);

        var persisted = Assert.Single(fixture.Db.McpAgentSessions);
        Assert.DoesNotContain(issue.AccessToken, persisted.AccessTokenHash, StringComparison.Ordinal);
        Assert.NotNull(issue.Configuration);
        Assert.Equal("1", issue.Configuration.SchemaVersion);
        Assert.Equal(
            "configured-model",
            issue.Configuration.Settings["llmModel"].GetString());
        Assert.NotNull(await fixture.Service.AuthenticateAsync(
            issue.AccessToken, issue.Session.SessionId, CancellationToken.None));

        var rotated = await fixture.Service.RenewAsync(issue.Session, CancellationToken.None);
        Assert.NotEqual(issue.AccessToken, rotated.AccessToken);
        Assert.NotNull(await fixture.Service.AuthenticateAsync(
            issue.AccessToken, issue.Session.SessionId, CancellationToken.None));

        fixture.Clock.Advance(TimeSpan.FromSeconds(31));

        Assert.Null(await fixture.Service.AuthenticateAsync(
            issue.AccessToken, issue.Session.SessionId, CancellationToken.None));
        Assert.NotNull(await fixture.Service.AuthenticateAsync(
            rotated.AccessToken, rotated.Session.SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Authenticate_ImmediatelyRejectsChangedGrantOrPackageDigest()
    {
        await using var fixture = await Fixture.CreateAsync();
        var issue = await fixture.EstablishAsync();

        fixture.Installation.Grant!.GrantRevision++;
        await fixture.Db.SaveChangesAsync();

        Assert.Null(await fixture.Service.AuthenticateAsync(
            issue.AccessToken, issue.Session.SessionId, CancellationToken.None));

        fixture.Installation.Grant.GrantRevision--;
        fixture.Package.PackageDigest = new string('b', 64);
        await fixture.Db.SaveChangesAsync();

        Assert.Null(await fixture.Service.AuthenticateAsync(
            issue.AccessToken, issue.Session.SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Establish_RejectsAReplayedOrIncorrectWorkloadIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Service.EstablishAsync(
                "wrong-token",
                fixture.Runtime.Id,
                fixture.Runtime.TickId,
                fixture.Installation.Id,
                fixture.Installation.BusinessId,
                fixture.Package.AgentId,
                fixture.Package.Version,
                CancellationToken.None));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            CSweetDbContext db,
            AgentPackageVersion package,
            AgentInstallation installation,
            AgentRuntimeInstance runtime,
            MutableTimeProvider clock,
            string workloadToken)
        {
            Db = db;
            Package = package;
            Installation = installation;
            Runtime = runtime;
            Clock = clock;
            WorkloadToken = workloadToken;
            Service = new McpAgentSessionService(
                db,
                new AgentEmployeeIdentityResolver(db),
                new AgentRuntimeSignalService(db),
                clock);
        }

        public CSweetDbContext Db { get; }
        public AgentPackageVersion Package { get; }
        public AgentInstallation Installation { get; }
        public AgentRuntimeInstance Runtime { get; }
        public MutableTimeProvider Clock { get; }
        public string WorkloadToken { get; }
        public McpAgentSessionService Service { get; }

        public Task<McpSessionIssue> EstablishAsync() =>
            Service.EstablishAsync(
                WorkloadToken,
                Runtime.Id,
                Runtime.TickId,
                Installation.Id,
                Installation.BusinessId,
                Package.AgentId,
                Package.Version,
                CancellationToken.None);

        public static async Task<Fixture> CreateAsync()
        {
            var db = new CSweetDbContext(
                new DbContextOptionsBuilder<CSweetDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            var source = new AgentPackageSource
            {
                Id = Guid.NewGuid(),
                RepositoryUrl = "https://github.com/example/agent",
                Host = "github.com",
                RepositoryOwner = "example",
                RepositoryName = "agent",
                DefaultBranch = "main",
                CreatedAt = clock.GetUtcNow(),
                UpdatedAt = clock.GetUtcNow()
            };
            var package = new AgentPackageVersion
            {
                Id = Guid.NewGuid(),
                PackageSourceId = source.Id,
                PackageSource = source,
                CommitSha = new string('1', 40),
                ManifestDigest = new string('a', 64),
                PackageDigest = new string('a', 64),
                ManifestJson = "{}",
                ManifestFileName = "csweet-plugin.json",
                AgentId = "com.example.agent",
                AgentName = "Example",
                Version = "1.0.0",
                PublisherId = "com.example",
                PublisherName = "Example",
                RuntimeType = "dotnet-project",
                Status = AgentPackageVersionStatus.Built,
                ImportedAt = clock.GetUtcNow()
            };
            var installation = new AgentInstallation
            {
                Id = Guid.NewGuid(),
                InstallationKey = Guid.NewGuid(),
                PackageVersionId = package.Id,
                PackageVersion = package,
                BusinessId = "organization-1",
                IsEnabled = true,
                RevisionStatus = PluginRevisionStatus.Active,
                CreatedAt = clock.GetUtcNow(),
                UpdatedAt = clock.GetUtcNow(),
                Grant = new AgentInstallationGrant
                {
                    Id = Guid.NewGuid(),
                    ProvidedCapabilitiesJson = "[]",
                    RequiredCapabilitiesJson = "[]",
                    EventSubscriptionsJson = "[]",
                    NetworkAccessJson = "[]",
                    ResourceLimitsJson = "{}",
                    GrantRevision = 1,
                    ApprovedAt = clock.GetUtcNow()
                },
                Schedule = new AgentSchedule
                {
                    Id = Guid.NewGuid(),
                    IsEnabled = true,
                    MaxRuntimeSeconds = 600
                },
                Configuration = new AgentInstallationConfiguration
                {
                    Id = Guid.NewGuid(),
                    SchemaVersion = "1",
                    SettingsJson = JsonSerializer.Serialize(new
                    {
                        llmProviderId = Guid.NewGuid().ToString("D"),
                        llmModel = "configured-model"
                    }),
                    CreatedAt = clock.GetUtcNow(),
                    UpdatedAt = clock.GetUtcNow()
                }
            };
            installation.Grant.AgentInstallationId = installation.Id;
            installation.Schedule.AgentInstallationId = installation.Id;
            installation.Configuration.AgentInstallationId = installation.Id;
            var workloadToken = "workload-token-" + Guid.NewGuid().ToString("N");
            var runtime = new AgentRuntimeInstance
            {
                Id = Guid.NewGuid(),
                TickId = Guid.NewGuid(),
                AgentInstallationId = installation.Id,
                AgentInstallation = installation,
                WorkloadTokenHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(workloadToken))),
                QueuedAt = clock.GetUtcNow(),
                RuntimeDeadlineAt = clock.GetUtcNow().AddMinutes(10)
            };
            runtime.TransitionTo(AgentRuntimeStatus.Starting, clock.GetUtcNow());
            runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, clock.GetUtcNow());
            db.AddRange(source, package, installation, runtime);
            await db.SaveChangesAsync();
            return new Fixture(db, package, installation, runtime, clock, workloadToken);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
