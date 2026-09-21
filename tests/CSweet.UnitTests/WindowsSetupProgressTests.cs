using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class WindowsSetupProgressTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecoveryProgressUsesCurrentWriteToValidateNewOwner()
    {
        using var owner = System.Diagnostics.Process.GetCurrentProcess();
        var currentWrite = DateTimeOffset.UtcNow;
        var previousAttemptStarted = new DateTimeOffset(owner.StartTime.ToUniversalTime(), TimeSpan.Zero).AddMinutes(-10);

        Assert.True(ExecutionFleetService.IsWindowsSetupOwnerAlive(owner.Id, currentWrite));
        Assert.False(ExecutionFleetService.IsWindowsSetupOwnerAlive(owner.Id, previousAttemptStarted));
    }

    [Fact]
    public void DeadOwnerAfterGracePeriodIsInterrupted() =>
        Assert.True(ExecutionFleetService.IsInterruptedWindowsSetupProgress(
            "running", Now.AddSeconds(-31), Now, ownerAlive: false));

    [Theory]
    [InlineData("running", true, -300)]
    [InlineData("running", false, -29)]
    [InlineData("completed", false, -300)]
    [InlineData("failed", false, -300)]
    [InlineData("restart-required", false, -300)]
    public void ActiveFreshOrTerminalProgressIsNotInterrupted(string state, bool ownerAlive, int observedOffsetSeconds) =>
        Assert.False(ExecutionFleetService.IsInterruptedWindowsSetupProgress(
            state, Now.AddSeconds(observedOffsetSeconds), Now, ownerAlive));
}
