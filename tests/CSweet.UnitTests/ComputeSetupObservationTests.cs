using System.Text.Json;
using CSweet.Api.Compute;
using CSweet.Domain.Compute;

namespace CSweet.UnitTests;

public sealed class ComputeSetupObservationTests
{
    [Fact]
    public void RecoveryUsesCurrentAttemptReceiptAndDetectsLostProcesses()
    {
        var directory = Path.Combine(Path.GetTempPath(), "compute-observation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var setup = new ComputeLocalSetup { State = "Running", HandoffHash = "current", UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
            void Result(string hash) => File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { attemptHash = hash, state = "Failed" }));
            Result("previous");
            File.WriteAllText(Path.Combine(directory, "process.json"), JsonSerializer.Serialize(new { attemptHash = "current", processId = 42, startedAt = DateTimeOffset.UtcNow }));
            Assert.True(ComputeSetupObservation.Read(setup, directory, (_, _) => true).InstallerRunning);
            Assert.Equal("setup_interrupted", ComputeSetupObservation.Read(setup, directory, (_, _) => false).FailureCode);
            Assert.Null(ComputeSetupObservation.Read(setup, directory, (_, _) => null).FailureCode);
            Result("current");
            Assert.Equal("local_setup_failed", ComputeSetupObservation.Read(setup, directory, (_, _) => true).FailureCode);
            setup.State = "Ready";
            Assert.Null(ComputeSetupObservation.Read(setup, directory).FailureCode);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void LegacyFinishedFailureIsNeverReportedAsAnActiveImageBuild()
    {
        var directory = Path.Combine(Path.GetTempPath(), "compute-observation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "installation.log"), "Ubuntu is installing\nError exporting vm\nTerminatingError(Install-ComputeLocalProvider.ps1)\nWindows PowerShell transcript end");
            Assert.Equal("image_export_failed", ComputeSetupObservation.Read(new() { State = "Running", UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1) }, directory).FailureCode);
        }
        finally { Directory.Delete(directory, true); }
    }
}
