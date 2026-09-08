using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Validates an approval-bound execution-profile upgrade before executable board state exists.</summary>
public static class WorkstreamProfileUpgrade
{
    public static async Task<WorkstreamProfileDefinitionRecord?> ResolveAsync(CSweetDbContext db, Workstream workstream,
        JsonElement changes, CancellationToken token)
    {
        if (!changes.TryGetProperty("profileUpgrade", out var upgrade)) return null;
        if (changes.EnumerateObject().Count() != 1 || upgrade.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A profile upgrade must be proposed separately from other workstream changes.");
        if (!upgrade.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String ||
            !upgrade.TryGetProperty("version", out var version) || !version.TryGetInt32(out var targetVersion) ||
            !upgrade.TryGetProperty("definitionDigest", out var digest) || digest.ValueKind != JsonValueKind.String ||
            workstream.ProfileVersion is null || key.GetString() != workstream.ProfileKey || targetVersion <= workstream.ProfileVersion)
            throw new ArgumentException("Upgrade requires the same profile key, a newer version, and its exact definition digest.");
        var target = await db.WorkstreamProfileDefinitions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Key == key.GetString() && x.Version == targetVersion && x.DefinitionDigest == digest.GetString() &&
            x.Status == "Active", token) ?? throw new InvalidOperationException("The approved target profile is not active or its digest changed.");
        var source = await db.WorkstreamProfileDefinitions.AsNoTracking().SingleAsync(x => x.Key == workstream.ProfileKey &&
            x.Version == workstream.ProfileVersion && x.DefinitionDigest == workstream.ProfileDefinitionDigest, token);
        using var oldDefinition = JsonDocument.Parse(source.DefinitionJson);
        using var newDefinition = JsonDocument.Parse(target.DefinitionJson);
        var allowed = new HashSet<string>(["version", "boardWorkflow", "orchestration"], StringComparer.Ordinal);
        var keys = oldDefinition.RootElement.EnumerateObject().Select(x => x.Name)
            .Union(newDefinition.RootElement.EnumerateObject().Select(x => x.Name));
        foreach (var property in keys.Where(x => !allowed.Contains(x)))
            if (!oldDefinition.RootElement.TryGetProperty(property, out var oldValue) ||
                !newDefinition.RootElement.TryGetProperty(property, out var newValue) || !JsonElement.DeepEquals(oldValue, newValue))
                throw new InvalidOperationException($"Profile upgrade changes '{property}', which requires an explicit project-data migration.");
        using var schema = JsonDocument.Parse(target.MetadataSchemaJson);
        using var data = JsonDocument.Parse(workstream.ProfileDataJson ?? "{}");
        WorkstreamProfileDefinitionValidator.ValidateProfileData(schema.RootElement, data.RootElement);
        var boards = await db.WorkBoards.AsNoTracking().Where(x => x.OrganizationId == workstream.OrganizationId &&
            x.WorkstreamId == workstream.Id && x.ArchivedAt == null).Select(x => x.Id).ToListAsync(token);
        // Board bootstrap can race the approved upgrade. An empty, unconfigured board
        // has no policy snapshots or assignments to migrate; its manager configures it
        // from the new workstream pin on the next reconciliation.
        if (await db.CoreWorkTasks.AsNoTracking().AnyAsync(x => x.BoardId.HasValue && boards.Contains(x.BoardId.Value), token) ||
            await db.WorkSprints.AsNoTracking().AnyAsync(x => boards.Contains(x.BoardId), token) ||
            await db.WorkOrchestrationPolicies.AsNoTracking().AnyAsync(x => boards.Contains(x.BoardId), token) ||
            await db.WorkSprintExecutions.AsNoTracking().AnyAsync(x => boards.Contains(x.BoardId), token))
            throw new InvalidOperationException("This workstream has planned or configured board state; its policy and assignment migration must be coordinated before upgrading the profile.");
        return target;
    }
}
