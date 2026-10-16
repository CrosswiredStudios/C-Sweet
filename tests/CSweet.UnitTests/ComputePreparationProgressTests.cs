using System.Text.Json;
using CSweet.Api.Compute;
using CSweet.Domain.Compute;

namespace CSweet.UnitTests;

public sealed class ComputePreparationProgressTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "compute-progress-" + Guid.NewGuid().ToString("N"));
    private readonly ComputeLocalSetup setup = new() { State = "Running", PreparationAttemptId = Guid.NewGuid() };
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-20);

    public ComputePreparationProgressTests() => Directory.CreateDirectory(directory);

    private void Write(Guid? attempt = null, string phase = "build-guest", string? message = null)
    {
        File.WriteAllText(Path.Combine(directory, "progress.json"), JsonSerializer.Serialize(new
        {
            version = 2, attemptId = attempt ?? setup.PreparationAttemptId, phase, message,
            startedAt = started, phaseStartedAt = started.AddMinutes(2), updatedAt = started.AddMinutes(2),
            activity = new[] { new { phase = "checking", at = started }, new { phase, at = started.AddMinutes(2) } }
        }));
    }

    [Fact]
    public void StructuredMilestonesAreAttemptScopedAndNeverReturnRawInstallerText()
    {
        Write(message: "private host path and credentials");
        var progress = Assert.IsType<CSweet.Contracts.Compute.ComputeSetupStatus>(ComputePreparationProgress.Read(setup, directory));
        Assert.Equal(3, progress.Milestone);
        Assert.Equal(started, progress.StartedAt);
        Assert.Equal(2, progress.Activity.Count);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(progress));
        Write(Guid.NewGuid());
        Assert.Null(ComputePreparationProgress.Read(setup, directory));
        Write(phase: "unrecognized-secret");
        Assert.Null(ComputePreparationProgress.Read(setup, directory));
    }

    [Fact]
    public void TerminalStateRetainsAttemptActivityAndPendingRetryDoesNotInheritIt()
    {
        Write();
        setup.State = "Failed";
        setup.ErrorCode = "local_setup_failed";
        setup.HandoffHash = null;
        Assert.NotNull(ComputePreparationProgress.Read(setup, directory));
        setup.State = "Ready";
        Assert.NotNull(ComputePreparationProgress.Read(setup, directory));
        setup.State = "Pending";
        Assert.Null(ComputePreparationProgress.Read(setup, directory));
    }

    [Fact]
    public void ProcessHealthIsIndependentOfMilestoneAgeAndUnknownDoesNotAllowRetry()
    {
        Write();
        var progress = ComputePreparationProgress.Read(setup, directory)!;
        var observed = ComputePreparationProgress.Observe(progress, setup, new(true, null), DateTimeOffset.UtcNow);
        Assert.Equal("Running", observed.Health);
        Assert.Equal(progress.LastProgressAt, observed.LastProgressAt);
        Assert.NotNull(observed.InstallerCheckedAt);
        var unknown = ComputePreparationProgress.Observe(progress, setup, new(null, null), DateTimeOffset.UtcNow);
        Assert.Equal("Unknown", unknown.Health);
        Assert.False(unknown.CanRetry);
        var failed = ComputePreparationProgress.Observe(progress, setup, new(false, "setup_interrupted"), DateTimeOffset.UtcNow);
        Assert.Equal("Failed", failed.State);
        Assert.True(failed.CanRetry);
        Assert.Equal(3, failed.Milestone);
    }

    [Fact]
    public void NewAttemptReceiptsWorkAfterSecretRevocationAndIgnoreLegacyTranscript()
    {
        setup.State = "Failed";
        File.WriteAllText(Path.Combine(directory, "process.json"), JsonSerializer.Serialize(new
        {
            attemptId = setup.PreparationAttemptId, processId = 42, startedAt = started
        }));
        Assert.True(ComputeSetupObservation.Read(setup, directory, (_, _) => true).InstallerRunning);
        Assert.Equal("setup_interrupted", ComputeSetupObservation.Read(setup, directory, (_, _) => false).FailureCode);
        setup.PreparationAttemptId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(directory, "installation.log"), "TerminatingError(Install-ComputeLocalProvider.ps1)\nWindows PowerShell transcript end");
        Assert.Null(ComputeSetupObservation.Read(setup, directory).FailureCode);
    }

    [Fact]
    public void MalformedOrPartialSnapshotDoesNotInventProgress()
    {
        File.WriteAllText(Path.Combine(directory, "progress.json"), "{\"version\":2");
        Assert.Null(ComputePreparationProgress.Read(setup, directory));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
