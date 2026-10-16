using System.Text.Json;
using CSweet.Contracts.Compute;
using CSweet.Domain.Compute;

namespace CSweet.Api.Compute;

internal static class ComputePreparationProgress
{
    private static readonly IReadOnlyDictionary<string, (int Milestone, string Message)> Phases =
        new Dictionary<string, (int, string)>(StringComparer.Ordinal)
        {
            ["checking"] = (1, "Checking requirements for Linux compute."),
            ["waiting"] = (1, "Another setup is preparing the shared image. This request will continue automatically."),
            ["install-openssh"] = (1, "Preparing the image-build tools."),
            ["download-packer"] = (1, "Downloading and verifying the image builder."),
            ["publish-guest"] = (1, "Preparing the Linux guest software."),
            ["resolve-ubuntu"] = (2, "Verifying the Ubuntu installation image."),
            ["download-ubuntu"] = (2, "Downloading the Ubuntu installation image."),
            ["prepare-packer"] = (3, "Preparing the Linux image builder."),
            ["build-guest"] = (3, "Installing Ubuntu and guest software in a Secure Boot VM."),
            ["guest-complete"] = (4, "The Linux image has been built."),
            ["finalize-image"] = (4, "Verifying and saving the reusable Linux image."),
            ["image-ready"] = (4, "The reusable Linux image is prepared."),
            ["cached"] = (4, "Reusing the verified Linux image."),
            ["check-service"] = (5, "Checking the compute service."),
            ["connect-service"] = (5, "Installing and connecting the compute service."),
            ["verify-readiness"] = (6, "Verifying compute readiness.")
        };

    internal static ComputeSetupStatus? Read(ComputeLocalSetup setup, string directory)
    {
        if (setup.PreparationAttemptId is null || setup.State == "Pending") return null;
        try
        {
            var file = new FileInfo(Path.Combine(directory, "progress.json"));
            if (!file.Exists || file.Length > 16384) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(file.FullName));
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 2 ||
                root.GetProperty("attemptId").GetGuid() != setup.PreparationAttemptId) return null;
            var phase = root.GetProperty("phase").GetString();
            if (phase is null || !Phases.TryGetValue(phase, out var definition)) return null;
            var started = root.GetProperty("startedAt").GetDateTimeOffset();
            var phaseStarted = root.GetProperty("phaseStartedAt").GetDateTimeOffset();
            var updated = root.GetProperty("updatedAt").GetDateTimeOffset();
            if (started > phaseStarted || phaseStarted > updated || updated > DateTimeOffset.UtcNow.AddSeconds(5)) return null;
            List<ComputePreparationEvent> activity = [];
            foreach (var entry in root.GetProperty("activity").EnumerateArray().TakeLast(24))
            {
                var key = entry.GetProperty("phase").GetString();
                var at = entry.GetProperty("at").GetDateTimeOffset();
                if (key is not null && Phases.TryGetValue(key, out var item) && at >= started && at <= updated &&
                    (activity.Count == 0 || at >= activity[^1].At)) activity.Add(new(key, item.Message, at));
            }
            return new(setup.State, definition.Message, updated, null, setup.ErrorCode)
            {
                AttemptId = setup.PreparationAttemptId, Phase = phase, Milestone = definition.Milestone,
                StartedAt = started, PhaseStartedAt = phaseStarted, Activity = activity,
                PreparationStep = definition.Milestone switch { 2 => 1, 3 or 4 => 2, 5 or 6 => 3, _ => 0 }
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException) { return null; }
    }

    internal static ComputeSetupStatus Observe(ComputeSetupStatus status, ComputeLocalSetup setup,
        ComputeSetupObservation observation, DateTimeOffset now)
    {
        var failure = setup.State == "Ready" ? null : setup.ErrorCode ?? observation.FailureCode;
        var state = failure is not null ? "Failed" : setup.State;
        var health = state switch
        {
            "Ready" => "Ready",
            "Pending" => "Queued",
            "Failed" => failure == "administrator_approval_required" ? "NeedsPermission" : "Stopped",
            _ => observation.InstallerRunning == true ? "Running" : "Unknown"
        };
        return status with
        {
            State = state, FailureCode = failure, Health = health, AttemptId = setup.PreparationAttemptId,
            Milestone = state == "Ready" ? 6 : status.Milestone,
            InstallerRunning = observation.InstallerRunning,
            InstallerCheckedAt = observation.InstallerRunning.HasValue ? now : null,
            CanRetry = state == "Failed" && observation.InstallerRunning != true
        };
    }
}
