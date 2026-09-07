using CSweet.Contracts.Setup;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class ExecutionCapacityAlertStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("offline")]
    [InlineData("ready")]
    public void ExistingOffice_GetsInitialConnectionWindow(string nodeStatus)
    {
        var state = new ExecutionCapacityAlertState();
        var capacity = Capacity(nodeStatus);

        state.Update(capacity, Now);
        Assert.Equal(ExecutionCapacityAlertStatus.Connecting, state.Status);
        state.Update(capacity, Now.AddSeconds(119));
        Assert.Equal(ExecutionCapacityAlertStatus.Connecting, state.Status);
        state.Update(capacity, Now.AddMinutes(2));
        Assert.Equal(ExecutionCapacityAlertStatus.Unavailable, state.Status);
    }

    [Fact]
    public void ReadyOffice_ClearsBannerAndSubsequentLossIsImmediate()
    {
        var state = new ExecutionCapacityAlertState();
        state.Update(Capacity(), Now);
        state.Update(Capacity() with { IsReady = true }, Now.AddSeconds(15));
        Assert.Equal(ExecutionCapacityAlertStatus.Hidden, state.Status);

        state.Update(Capacity(), Now.AddSeconds(30));
        Assert.Equal(ExecutionCapacityAlertStatus.Unavailable, state.Status);
        state.Update(Capacity() with { IsReady = true }, Now.AddSeconds(45));
        Assert.Equal(ExecutionCapacityAlertStatus.Hidden, state.Status);
    }

    [Theory]
    [InlineData("pendingapproval")]
    [InlineData("revoked")]
    [InlineData("draining")]
    [InlineData("rejected")]
    public void OfficeRequiringAction_ShowsErrorImmediately(string nodeStatus)
    {
        var state = new ExecutionCapacityAlertState();
        state.Update(Capacity(nodeStatus), Now);
        Assert.Equal(ExecutionCapacityAlertStatus.Unavailable, state.Status);
    }

    [Theory]
    [InlineData("launch-gate")]
    [InlineData("image-policy")]
    public void InvalidPolicy_ShowsErrorImmediately(string failedCheck)
    {
        var state = new ExecutionCapacityAlertState();
        var capacity = Capacity();
        state.Update(capacity with
        {
            Checks = capacity.Checks.Select(check => check.Key == failedCheck
                ? check with { Status = "action-required" } : check).ToArray()
        }, Now);
        Assert.Equal(ExecutionCapacityAlertStatus.Unavailable, state.Status);
    }

    [Fact]
    public void MissingOffice_ShowsErrorImmediately()
    {
        var state = new ExecutionCapacityAlertState();
        state.Update(Capacity() with { Nodes = [] }, Now);
        Assert.Equal(ExecutionCapacityAlertStatus.Unavailable, state.Status);
    }

    [Fact]
    public void ExpiredCertificate_ShowsErrorImmediately()
    {
        var state = new ExecutionCapacityAlertState();
        var capacity = Capacity();
        state.Update(capacity with
        {
            Nodes = [capacity.Nodes[0] with { CertificateExpiresAt = Now }]
        }, Now);
        Assert.Equal(ExecutionCapacityAlertStatus.Unavailable, state.Status);
    }

    [Fact]
    public void ConnectivityFailure_HidesBannerWithoutRestartingGracePeriod()
    {
        var state = new ExecutionCapacityAlertState();
        state.Update(Capacity(), Now);
        state.Hide();
        Assert.Equal(ExecutionCapacityAlertStatus.Hidden, state.Status);
        state.Update(Capacity(), Now.AddMinutes(2));
        Assert.Equal(ExecutionCapacityAlertStatus.Unavailable, state.Status);
    }

    private static ExecutionCapacityOnboardingResponse Capacity(string nodeStatus = "offline") => new(
        "remote", false, 0, 0, false, "windows", "x64", "Waiting for Office", null,
        [new ExecutionNodeSummaryResponse(
            Guid.NewGuid(), Guid.NewGuid(), "Office", "machine", "windows", "x64", "1.0", "1.0",
            nodeStatus, "thumbprint", Now.AddDays(1), 4, 8192, 16384, 2, Now.AddMinutes(-1), [],
            new Dictionary<string, string>())],
        [new ExecutionCapacityCheckResponse("launch-gate", "Launch", "passed", "Enabled"),
         new ExecutionCapacityCheckResponse("image-policy", "Images", "passed", "Configured")]);
}
