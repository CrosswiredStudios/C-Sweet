using CSweet.AgentRuntime.Guest;

namespace CSweet.UnitTests;

public sealed class GuestArtifactMaterializerTests
{
    [Fact]
    public void WorkloadModesKeepArtifactRootOwnedAndGroupReadable()
    {
        var directory = GuestArtifactMaterializer.SanitizeModeForWorkload(default, isDirectory: true);
        var data = GuestArtifactMaterializer.SanitizeModeForWorkload(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            isDirectory: false);
        var executable = GuestArtifactMaterializer.SanitizeModeForWorkload(
            UnixFileMode.UserRead | UnixFileMode.UserExecute,
            isDirectory: false);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute,
            directory);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead, data);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute,
            executable);
        Assert.Equal(0, (int)(directory | data | executable) &
            (int)(UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute |
                  UnixFileMode.GroupWrite));
    }
}
