using CSweet.Api.Compute;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeDashboardTests
{
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
        Assert.Null(await ComputeDashboardEndpoints.ReadAsync(f.Db, Guid.NewGuid(), user, root, root, default));
        Assert.Null(await ComputeDashboardEndpoints.ReadAsync(f.Db, f.Organization, Guid.NewGuid(), root, root, default));
        member.IsActive = false; await f.Db.SaveChangesAsync();
        Assert.Null(await ComputeDashboardEndpoints.ReadAsync(f.Db, f.Organization, user, root, root, default));
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
