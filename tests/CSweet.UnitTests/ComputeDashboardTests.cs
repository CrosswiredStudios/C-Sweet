using CSweet.Api.Compute;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeDashboardTests
{
    [Fact]
    public async Task InstallerFailurePublishesOneComputeChangeAndLateDeliveryCannotRestoreItAfterRetry()
    {
        await using var f = new ComputeBrokerTests.Fixture();
        await f.SeedAsync();
        f.Db.CoreOrganizationUsers.Add(new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            ApplicationUserId = Guid.NewGuid(), EmployeeType = EmployeeType.Human, IsActive = true,
            PermissionLevel = OrganizationPermissionLevel.Owner });
        var observed = new ComputeLocalSetup { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            State = "Running", PreparationAttemptId = Guid.NewGuid(), HandoffHash = "old", HandoffExpiresAt = DateTimeOffset.UtcNow };
        f.Db.Add(observed);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        var events = f.Db.ApplicationRealtimeOutbox.Where(x => x.OrganizationId == f.Organization && x.EventType == AppRealtimeEvents.ComputeChanged);
        var initial = await events.CountAsync();
        await ComputeLocalSetupWorker.FailAsync(f.Db, observed, "compute_checks_build_failed", default);
        await using var restarted = new CSweetDbContext(f.Options);
        var setup = await restarted.Set<ComputeLocalSetup>().SingleAsync();
        Assert.Equal("Failed", setup.State);
        Assert.Equal("compute_checks_build_failed", setup.ErrorCode);
        Assert.Null(setup.HandoffHash);
        Assert.Null(setup.HandoffExpiresAt);
        await ComputeLocalSetupWorker.FailAsync(restarted, observed, "setup_interrupted", default);
        Assert.Equal(initial + 1, await events.CountAsync());
        setup.State = "Pending";
        setup.ErrorCode = null;
        setup.Revision++;
        await restarted.SaveChangesAsync();
        Assert.Equal(initial + 2, await events.CountAsync());
        setup.State = "Running";
        setup.PreparationAttemptId = Guid.NewGuid();
        setup.Revision++;
        await restarted.SaveChangesAsync();
        await ComputeLocalSetupWorker.FailAsync(restarted, observed, "setup_interrupted", default);
        Assert.Equal("Running", (await f.Db.Set<ComputeLocalSetup>().AsNoTracking().SingleAsync()).State);
        Assert.Equal(initial + 3, await events.CountAsync());
        // This context still tracks the earlier attempt; a current installer result must still reach the owner.
        await ComputeLocalSetupWorker.FailAsync(f.Db, setup, "compute_service_start_failed", default);
        Assert.Equal("compute_service_start_failed", (await restarted.Set<ComputeLocalSetup>().AsNoTracking().SingleAsync()).ErrorCode);
        Assert.Equal(initial + 4, await events.CountAsync());
    }

    [Fact]
    public async Task Runtime_update_requires_owner_and_idle_compute_and_preserves_instances()
    {
        await using var f=new ComputeBrokerTests.Fixture();await f.SeedAsync();
        var env=await ComputeWorkloadTests.ReadyAsync(f);var user=Guid.NewGuid();
        var member=new OrganizationUser{Id=Guid.NewGuid(),OrganizationId=f.Organization,ApplicationUserId=user,
            EmployeeType=EmployeeType.Human,IsActive=true,PermissionLevel=OrganizationPermissionLevel.Contributor};
        f.Db.CoreOrganizationUsers.Add(member);
        var setup=new ComputeLocalSetup{Id=Guid.NewGuid(),OrganizationId=f.Organization,State="Ready",HandoffHash="old",HandoffExpiresAt=DateTimeOffset.UtcNow};
        f.Db.Add(setup);await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>ComputeDashboardEndpoints.QueueRuntimeUpdateAsync(f.Db,f.Organization,user,default));
        member.PermissionLevel=OrganizationPermissionLevel.Owner;await f.Db.SaveChangesAsync();
        var operation=await f.Db.ComputeOperations.SingleAsync();operation.Status="Dispatching";await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>ComputeDashboardEndpoints.QueueRuntimeUpdateAsync(f.Db,f.Organization,user,default));
        Assert.Equal("Ready",setup.State);operation.Status="Completed";await f.Db.SaveChangesAsync();
        await ComputeDashboardEndpoints.QueueRuntimeUpdateAsync(f.Db,f.Organization,user,default);
        Assert.Equal("Pending",setup.State);Assert.Null(setup.HandoffHash);Assert.Null(setup.HandoffExpiresAt);
        var retained=await f.Db.ComputeEnvironments.SingleAsync();Assert.Equal(env.Id,retained.Id);Assert.Null(retained.TeardownConfirmedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>ComputeDashboardEndpoints.QueueRuntimeUpdateAsync(f.Db,f.Organization,user,default));
        Assert.Contains(f.Db.AuditOutbox,x=>x.RequestJson.Contains("runtime-update-requested"));
    }

    [Fact]
    public async Task DashboardRequiresActiveHumanMembershipAndDoesNotExposeAnotherBusiness()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); await f.Send();
        var user = Guid.NewGuid();
        var member = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Organization, ApplicationUserId = user,
            EmployeeType = EmployeeType.Human, IsActive = true };
        f.Db.CoreOrganizationUsers.Add(member);
        f.Db.ComputeEnvironments.Add(new() { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), DesiredEnvironmentKey = "private-other-business" });
        await f.Db.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var dashboard = await ComputeDashboardEndpoints.ReadAsync(f.Db, f.Organization, user, root, root, default);
        Assert.NotNull(dashboard);
        var resource = Assert.Single(dashboard.Resources);
        Assert.Equal("linux", resource.OperatingSystem);
        Assert.Equal(2, resource.CpuCount);
        Assert.Equal((await f.Db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == f.Installation)).Id, resource.EmployeeId);
        Assert.False(resource.LocalLinkRequested);
        Assert.Null(await ComputeDashboardEndpoints.ReadAsync(f.Db, Guid.NewGuid(), user, root, root, default));
        Assert.Null(await ComputeDashboardEndpoints.ReadAsync(f.Db, f.Organization, Guid.NewGuid(), root, root, default));
        member.IsActive = false; await f.Db.SaveChangesAsync();
        Assert.Null(await ComputeDashboardEndpoints.ReadAsync(f.Db, f.Organization, user, root, root, default));
    }

    [Fact]
    public async Task OfficeDashboardShowsWhoIsWaitingUntilTheirWorkspaceIsPlaced()
    {
        await using var f = new ComputeBrokerTests.Fixture();
        await f.SeedAsync();
        var user = Guid.NewGuid();
        f.Db.CoreOrganizationUsers.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            ApplicationUserId = user, EmployeeType = EmployeeType.Human, IsActive = true });
        var employee = await f.Db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == f.Installation);
        employee.DisplayName = "Victor Lin";
        var requestedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        f.Db.Add(new OfficeComputeCapacityRequest { OrganizationId = f.Organization, ProjectId = f.Workstream,
            InstallationId = f.Installation, RequestedAt = requestedAt });
        await f.Db.SaveChangesAsync();

        async Task<CSweet.Contracts.Compute.ComputeDashboard> Read() =>
            (await ComputeDashboardEndpoints.ReadAsync(f.Db, f.Organization, user, "", "", default))!;
        var pending = await Read();
        Assert.Equal("Pending", pending.Setup!.State);
        Assert.Equal(new CSweet.Contracts.Compute.OfficeComputeCapacityStatus(false, 1, "Victor Lin", requestedAt),
            pending.Setup.OfficeCapacity);

        await f.Send();
        var placed = await Read();
        Assert.Single(placed.Resources);
        Assert.Equal(0, placed.Setup!.OfficeCapacity!.WaitingEmployees);
        Assert.Equal("Unavailable", placed.Setup.State);
    }

    [Fact]
    public void SetupReportsObservedDownloadProgressButDoesNotUseItAfterFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "compute-dashboard-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = Path.Combine(root, "CSweet.Isolation", "artifacts", "linux-images", "cache");
            Directory.CreateDirectory(cache);
            var image = Path.Combine(cache, "ubuntu-test-live-server-amd64.iso");
            File.WriteAllBytes(image, new byte[128]);
            using var download = new FileStream(image, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var setup = new ComputeLocalSetup { Id = Guid.NewGuid(), State = "Running", UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
            var progress = ComputeDashboardEndpoints.ReadProgress(setup, Path.Combine(root, "csweet"), root);
            Assert.Equal(128, progress.DownloadedBytes);
            Assert.Contains("Downloading", progress.Stage);
            Assert.True(progress.LastProgressAt > setup.UpdatedAt);
            download.Write(new byte[128]); download.Flush();
            Assert.Equal(256, ComputeDashboardEndpoints.ReadProgress(setup, Path.Combine(root, "csweet"), root).DownloadedBytes);
            var setupDirectory = Path.Combine(root, "CSweet", "ComputeSetup", setup.Id.ToString("N"));
            Directory.CreateDirectory(setupDirectory);
            File.WriteAllText(Path.Combine(setupDirectory, "installation.log"),
                "private host path and credential must never appear\nUbuntu is installing in a Secure Boot VM. This can take 10-30 minutes.");
            var building = ComputeDashboardEndpoints.ReadProgress(setup, Path.Combine(root, "csweet"), root);
            Assert.Equal(2, building.PreparationStep);
            Assert.Null(building.DownloadedBytes);
            Assert.Equal("Ubuntu is installing in a Secure Boot VM.", building.Stage);
            setup.State = "Failed"; setup.ErrorCode = "local_setup_failed";
            var failed = ComputeDashboardEndpoints.ReadProgress(setup, Path.Combine(root, "csweet"), root);
            Assert.Null(failed.DownloadedBytes);
            Assert.Equal("Preparation failed", failed.Stage);
            Assert.Equal("local_setup_failed", failed.FailureCode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
