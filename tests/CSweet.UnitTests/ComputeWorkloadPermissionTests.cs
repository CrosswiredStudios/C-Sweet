using System.Security.AccessControl;
using System.Security.Principal;
using CSweet.Compute.Runtime;
using CSweet.Compute.HyperV;

namespace CSweet.UnitTests;

public sealed class ComputeWorkloadPermissionTests
{
    [Fact]
    public void Worker_identity_matches_the_actual_hypervisor_account_after_vm_removal()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal("S-1-5-83-1-1762630317-1165983835-2706050178-2983744263",
            HyperVWorkloadProtection.WorkerIdentity(Guid.Parse("690f9aad-805b-457f-820c-4ba10753d8b1")).Value);
    }
    [Fact]
    public void Only_the_exact_vm_can_write_its_workload_and_cannot_change_permissions()
    {
        if (!OperatingSystem.IsWindows()) return;
        var vm = new SecurityIdentifier("S-1-5-83-0-1-2-3-4");
        var other = new SecurityIdentifier("S-1-5-83-0-5-6-7-8");
        var acl = new FileSecurity();
        acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        acl.AddAccessRule(new(vm, FileSystemRights.Modify, AccessControlType.Allow));
        WindowsComputeProtectedPaths.VerifyRules(acl, workloadIdentity: vm);
        Assert.Throws<UnauthorizedAccessException>(() => WindowsComputeProtectedPaths.VerifyRules(acl));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsComputeProtectedPaths.VerifyRules(acl, workloadIdentity: other));
        acl.AddAccessRule(new(vm, FileSystemRights.ChangePermissions, AccessControlType.Allow));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsComputeProtectedPaths.VerifyRules(acl, workloadIdentity: vm));
    }
}
