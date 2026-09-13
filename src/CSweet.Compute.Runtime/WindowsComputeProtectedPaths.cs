using System.Security.AccessControl;
using System.Security.Principal;
namespace CSweet.Compute.Runtime;

public static class WindowsComputeProtectedPaths
{
    public static void Verify(string path)
    {
        if(!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Protected compute paths require Windows ACLs.");
        if(!Path.IsPathFullyQualified(path)) throw new UnauthorizedAccessException("A protected absolute path is required.");
        var full=Path.GetFullPath(path);
        FileSystemInfo entry=Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        if(!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("A protected runtime path is missing or redirects elsewhere.");
        var security=entry is DirectoryInfo directory ? (FileSystemSecurity)directory.GetAccessControl() : ((FileInfo)entry).GetAccessControl();
        VerifyRules(security);
        for(var parent=Directory.GetParent(full);parent is not null;parent=parent.Parent)
        {
            if((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Protected runtime paths must not traverse links.");
            VerifyRules(parent.GetAccessControl(), ancestor: true);
        }
    }
    public static void VerifyRules(FileSystemSecurity security, bool ancestor = false, SecurityIdentifier? workloadIdentity = null)
    {
        if(!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Protected compute paths require Windows ACLs.");
    const FileSystemRights MutatingRights = FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        static bool Trusted(SecurityIdentifier sid)=>sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
            sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"; // TrustedInstaller
        if(security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted(owner))
            throw new UnauthorizedAccessException("Runtime state and payloads must be owned by SYSTEM or Administrators.");
        var dangerous = ancestor ? FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership | FileSystemRights.Delete : MutatingRights;
        foreach(FileSystemAccessRule rule in security.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow && (!ancestor || !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) &&
                (rule.FileSystemRights & dangerous)!=0 &&
                !Trusted((SecurityIdentifier)rule.IdentityReference) &&
                !(workloadIdentity is not null && rule.IdentityReference.Equals(workloadIdentity) &&
                  (rule.FileSystemRights & (FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) == 0))
                throw new UnauthorizedAccessException("An unprivileged identity can modify a protected runtime path.");
    }
}
