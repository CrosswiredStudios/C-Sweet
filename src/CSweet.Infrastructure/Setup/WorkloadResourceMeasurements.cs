using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

internal static class WorkloadResourceMeasurements
{
    // Only the authenticated guest supervisor supplies peak bytes. Neither model estimates,
    // command exit 137 nor text in a runtime log can create a successful measurement.
    internal static async Task RecordAsync(CSweetDbContext db, Guid runtimeId, long peakBytes, CancellationToken token)
    {
        if (peakBytes <= 0) return;
        var runtime = await db.AgentRuntimeInstances.SingleAsync(x => x.Id == runtimeId, token);
        if (runtime.ResourceMeasurementsRecordedAt is not null) return;
        var settings = await db.AgentRuntimeGlobalSettings.AsNoTracking().SingleAsync(token);
        var attempts = await db.AgentWorkAttempts.AsNoTracking().Include(x => x.AgentWorkItem)
            .Where(x => x.RuntimeInstanceId == runtimeId && x.WorkSucceeded == true && x.AgentWorkItem!.ResourceProfileKey != null).Take(64).ToArrayAsync(token);
        foreach (var key in attempts.Select(x => x.AgentWorkItem!.ResourceProfileKey!).Distinct())
        {
            var profile = await db.WorkloadResourceProfiles.SingleOrDefaultAsync(x => x.Key == key, token);
            if (profile is null) continue;
            profile.PeakWorkloadMemoryBytes = Math.Max(profile.PeakWorkloadMemoryBytes, peakBytes);
            profile.SuccessfulSamples++; profile.Revision++; profile.UpdatedAt = DateTimeOffset.UtcNow;
            // Workload uses at most 75% of guest memory. Add a further 30% burst margin,
            // round upward to an authorized tier, and never downsize an active operation.
            var desiredMb = (int)Math.Min(int.MaxValue, Math.Ceiling(peakBytes / (1024d * 1024) / .75 * 1.30));
            var tier = WorkloadResourcePolicy.Tiers.FirstOrDefault(x => x >= desiredMb && x <= settings.CodingMaximumMemoryMb);
            if (tier > 0) profile.MemoryMb = Math.Max(profile.MemoryMb, tier);
        }
        runtime.ResourceMeasurementsRecordedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
    }
}
