using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed partial class ExecutionFleetServiceTests
{
    [Fact]
    public async Task PrivateCombinedSetupRequiresItsApprovedEnrollmentAndExactCertifiedImages()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var scope = new ExecutionFleetOptions();
        var images = new AgentRuntimeManagerOptions { BuilderGuestImageDigest = Digest('a'), RuntimeGuestImageDigest = Digest('a'), RequiredCertificationSuiteVersion = "production-v1" };
        var clock = new MutableTimeProvider(Now);
        await using var initial = new CSweetDbContext(options);
        var registry = CreateFleet(initial, clock);
        await new SetupService(initial).EnsureSeededAsync();
        async Task<Guid> Register()
        {
            var enrollment = (await registry.CreateEnrollmentAsync()).Enrollment!;
            var req = Claim(enrollment.EnrollmentToken!);
            var claim = await registry.ClaimNodeAsync(req with { OperatingSystem = "windows", MachineName = "same-host",
                Providers = [req.Providers.Single() with { ProviderId = "hyperv-gen2" }] });
            Assert.True(claim.Succeeded, claim.ErrorCode);
            Assert.True((await registry.ApproveNodeAsync(claim.OfficeId!.Value)).Succeeded);
            (await initial.ExecutionNodes.SingleAsync(n => n.Id == claim.OfficeId)).LastHeartbeatAt = Now;
            await initial.SaveChangesAsync();
            return enrollment.Id;
        }
        var installed = await Register();
        await Register();
        scope.CombinedOfficeEnrollmentId = installed;
        await using var restarted = new CSweetDbContext(options, fleetOptions: Options.Create(scope), runtimeOptions: Options.Create(images));
        var fleet = CreateFleet(restarted, clock, fleetOptions: scope);
        var setup = new SetupService(restarted, fleet);
        Assert.True((await setup.CompleteStepAsync("agent-execution")).Succeeded);
        Assert.Equal(1, (await fleet.GetOnboardingStatusAsync()).ReadyNodeCount);
        var enrollmentRow = await restarted.ExecutionNodeEnrollments.SingleAsync(e => e.Id == installed);
        var provider = await restarted.ExecutionNodeProviders.SingleAsync(p => p.ExecutionNodeId == enrollmentRow.ExecutionNodeId);
        provider.GuestImageDigest = Digest('c');
        await restarted.SaveChangesAsync();
        Assert.False(await fleet.IsReadyAsync());
        provider.GuestImageDigest = Digest('a');
        enrollmentRow.Status = ExecutionEnrollmentStatus.Revoked;
        await restarted.SaveChangesAsync();
        Assert.False((await fleet.GetOnboardingStatusAsync()).IsReady);
        Assert.Empty(await restarted.OfficesForPlacement(Now).ToListAsync());
        Assert.False(scope.PublicLaunchEnabled);
    }

    [Fact]
    public async Task CombinedOfficeInstallApprovesOnlyTheLocalOneUseEnrollment()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var db = CreateDb();
        await new SetupService(db).EnsureSeededAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var fleet = CreateFleet(db, new MutableTimeProvider(Now), configuration);
        await fleet.SelectOnboardingModeAsync(new("remote"));
        var enrollment = (await fleet.CreateEnrollmentAsync()).Enrollment!;
        configuration["CSweet:CombinedInstallOfficeEnrollmentId"] = enrollment.Id.ToString("D");
        var local = Claim(enrollment.EnrollmentToken!) with { MachineName = Environment.MachineName, OperatingSystem = "windows" };
        Assert.Equal("combined_machine_mismatch", (await fleet.ClaimNodeAsync(local with { MachineName = "other-host" })).ErrorCode);
        var claim = await fleet.ClaimNodeAsync(local);
        Assert.True(claim.Succeeded, claim.ErrorCode);
        Assert.NotNull((await db.ExecutionNodes.SingleAsync()).ApprovedAt);
        Assert.Equal("invalid_enrollment", (await fleet.ClaimNodeAsync(local)).ErrorCode);
    }

    [Fact]
    public async Task FirstOfficeCreatedByCombinedSetupBindsOnlyToAnAdministratorsBusiness()
    {
        await using var db = CreateDb();
        await new SetupService(db).EnsureSeededAsync();
        var reserved = await FirstOfficeBusiness.ReserveAsync(db, null, default);
        var admin = new CSweet.Infrastructure.Auth.ApplicationUser { Id = Guid.NewGuid(), UserName = "setup-admin" };
        var role = new Microsoft.AspNetCore.Identity.IdentityRole<Guid>(CSweet.Infrastructure.Auth.AuthenticationService.AdministratorRole)
            { Id = Guid.NewGuid() };
        db.Users.Add(admin);
        db.Roles.Add(role);
        db.UserRoles.Add(new() { UserId = admin.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        var audit = new TestAuditEventWriter();
        var organizations = new CoreOrganizationService(db, audit, new RoleService(db, audit));
        var other = await organizations.CreateAsync(new("Other business", null, null, null, null, null), default, Guid.NewGuid());
        Assert.NotEqual(reserved, other.Organization!.Id);
        var studio = await organizations.CreateAsync(new("Game studio", null, null, null, null, null), default, admin.Id);
        Assert.Equal(reserved, studio.Organization!.Id);
        Assert.Null((await db.SystemConfigurations.SingleAsync()).FirstOfficeBusinessId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssistedInstallReservesOnceAndClaimsOnlyTheRemainingOfficeCapacity(bool installBeforeBusiness)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        await new SetupService(db).EnsureSeededAsync();
        var user = Guid.NewGuid();
        var business = Guid.NewGuid();
        var image = "sha256:" + new string('a', 64);
        if (!installBeforeBusiness)
        {
            db.CoreOrganizations.Add(new() { Id = business, Name = "Inkling Games" });
            db.CoreOrganizationUsers.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, ApplicationUserId = user,
                EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner, IsActive = true });
        }
        await db.SaveChangesAsync();
        var dependencies = new ServiceCollection().AddSingleton(Options.Create(new OfficeComputeCatalogOptions
            { ApprovedImages = installBeforeBusiness ? [] : [new(business, image, ["docker", "docker-apps-v1"])] }));
        dependencies.AddHttpClient(string.Empty).ConfigurePrimaryHttpMessageHandler(() => new OfficeReleaseFixture(image));
        using var services = dependencies.BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CSweet:ExecutionGateway:PublicUrl"] = "https://office.example.test/",
            ["CSweet:ExecutionGateway:PublicCertificateSha256"] = new string('a', 64)
        }).Build();
        var capacity = LocalOfficeCapacityCalculator.Calculate(24, 64L << 30, 256L << 30, true);
        var fleet = CreateFleet(db, clock, configuration, capacityProbe: new FixedCapacityProbe(capacity), services: services,
            fleetOptions: new() { PublicLaunchEnabled = true, AllowUnpinnedDevelopmentImages = installBeforeBusiness,
                WindowsPackageOverrideUrl = "https://downloads.example.test/csweet-office.msi" });
        var selected = capacity.Presets.Single(x => x.Key == "balanced");
        var created = await fleet.CreateLocalSetupSessionAsync(new("balanced", selected.CpuCount, selected.MemoryMb, selected.DiskMb), user);
        Assert.True(created.Succeeded);
        var handoff = new Uri(created.Session!.LaunchUri!).Fragment["#handoff=".Length..];
        var machine = Environment.MachineName;
        var redeemed = await fleet.RedeemLocalSetupSessionAsync(new(Uri.UnescapeDataString(handoff), machine, "windows", "x64", "0.17.1"));
        Assert.True(redeemed.Succeeded);
        var request = new OfficeComputeSetupRequest(created.Session.Id, redeemed.SetupReceipt!, machine, "windows", "x64", "0.17.1", image);
        Assert.Null(await fleet.PrepareHostedComputeAsync(request with { SetupReceipt = "wrong" }, default));
        Assert.False((await fleet.PrepareHostedComputeAsync(request with { ImageDigest = "sha256:" + new string('b', 64) }, default))!.Enabled);
        var plan = await fleet.PrepareHostedComputeAsync(request, default);
        if (installBeforeBusiness) business = Assert.IsType<Guid>(plan?.OrganizationId);
        Assert.Equal(new OfficeComputeSetupPlan(true, null, business, 1, 2, 2048, 20480), plan);
        var audit = new TestAuditEventWriter();
        var organizations = new CoreOrganizationService(db, audit, new RoleService(db, audit));
        if (installBeforeBusiness)
        {
            var other = await organizations.CreateAsync(new("Other owner", null, null, null, null, null), default, Guid.NewGuid());
            Assert.NotEqual(business, other.Organization!.Id);
        }
        db.ChangeTracker.Clear();
        Assert.Equal(plan, await fleet.PrepareHostedComputeAsync(request, default));
        if (!installBeforeBusiness) Assert.Equal(business, (await db.ComputeTemplates.SingleAsync()).OrganizationId);
        var claim = Claim(redeemed.EnrollmentToken!, officeVersion: "0.17.1") with
        {
            MachineName = machine, OperatingSystem = "windows", Architecture = "x64",
            AssistedSetupSessionId = created.Session.Id, AllocatableCpuCount = redeemed.AllocatableCpuCount,
            AllocatableMemoryMb = redeemed.AllocatableMemoryMb, AllocatableDiskMb = redeemed.AllocatableDiskMb,
            MaximumConcurrentWorkloads = redeemed.MaximumConcurrentWorkloads
        };
        Assert.Equal("assisted_allocation_mismatch", (await fleet.ClaimNodeAsync(claim)).ErrorCode);
        var result = await fleet.ClaimNodeAsync(claim with { AllocatableCpuCount = claim.AllocatableCpuCount - 2,
            AllocatableMemoryMb = claim.AllocatableMemoryMb - 2048, AllocatableDiskMb = claim.AllocatableDiskMb - 20480,
            MaximumConcurrentWorkloads = claim.MaximumConcurrentWorkloads - 1 });
        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.Null(await fleet.PrepareHostedComputeAsync(request, default));
        if (installBeforeBusiness)
        {
            Assert.Empty(db.ComputeTemplates);
            var first = await organizations.CreateAsync(new("Game studio", null, null, null, null, null), default, user);
            Assert.Equal(business, first.Organization!.Id);
            var next = await organizations.CreateAsync(new("Second studio", null, null, null, null, null), default, user);
            Assert.NotEqual(business, next.Organization!.Id);
            Assert.Null((await db.SystemConfigurations.SingleAsync()).FirstOfficeBusinessId);
            // Emulate a fresh authenticated heartbeat with a real Office verification key.
            using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            using var cert = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Office", key,
                System.Security.Cryptography.HashAlgorithmName.SHA256).CreateSelfSigned(Now.AddDays(-1), Now.AddDays(1));
            var office = await db.ExecutionNodes.SingleAsync();
            office.IssuedCertificateBase64 = Convert.ToBase64String(cert.RawData);
            office.CertificateThumbprint = cert.Thumbprint;
            var offer = new CSweet.Office.Contracts.ControlPlane.OfficeComputeOffer { OrganizationId = business.ToString("D"),
                TemplateId = OfficeComputePlacement.TemplateId(image), ImageDigest = image, MaximumEnvironments = 1,
                CpuCount = 2, MemoryMib = 2048, DiskMib = 20480 };
            offer.Features.Add(["docker", "docker-apps-v1"]);
            await OfficeComputePlacement.ApplyOfferAsync(db, office, offer, Now, default);
            await db.SaveChangesAsync();
            Assert.Equal(offer.TemplateId, office.ComputeTemplateId);
            Assert.Equal(business, (await db.ComputeNodes.SingleAsync()).OrganizationId);
        }
    }

    private sealed class OfficeReleaseFixture(string image) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { RequestMessage = request,
                Content = System.Net.Http.Json.JsonContent.Create(new { officeVersion = "0.17.1", signing = "development",
                    requiresHostCertification = true, guestBrokerProtocolVersion = "1.1",
                    computeImage = new { imageDigest = image, features = new[] { "docker", "docker-apps-v1" } } }) });
    }
}
