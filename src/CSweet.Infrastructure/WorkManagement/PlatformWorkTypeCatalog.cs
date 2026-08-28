using CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Immutable platform-owned work type and approval policy catalog.</summary>
public static class PlatformWorkTypeCatalog
{
    public const string ProviderKey = WorkItemTypeProviderKeys.Platform;
    public const long Revision = 1;

    private static readonly IReadOnlyList<WorkItemApprovalPolicyDefinition> Policies =
    [
        new(WorkItemApprovalPolicyKeys.SoftwareArchitectureReviewV1,
            "Architecture review", ProviderKey, "software-architect")
    ];

    private static readonly IReadOnlyList<WorkItemTypeDefinition> Types =
    [
        new(WorkItemTypeKeys.GeneralInitiativeV1, "General initiative", WorkItemKinds.Initiative,
            [WorkBoardProfileKeys.GeneralWorkV1], [], ProviderKey, []),
        new(WorkItemTypeKeys.GeneralEpicV1, "General epic", WorkItemKinds.Epic,
            [WorkBoardProfileKeys.GeneralWorkV1], [WorkItemTypeKeys.GeneralInitiativeV1], ProviderKey, []),
        new(WorkItemTypeKeys.GeneralStoryV1, "General story", WorkItemKinds.Story,
            [WorkBoardProfileKeys.GeneralWorkV1], [WorkItemTypeKeys.GeneralEpicV1], ProviderKey, []),
        new(WorkItemTypeKeys.GeneralTaskV1, "General task", WorkItemKinds.Task,
            [WorkBoardProfileKeys.GeneralWorkV1], [WorkItemTypeKeys.GeneralStoryV1, WorkItemTypeKeys.GeneralTaskV1], ProviderKey, []),
        new(WorkItemTypeKeys.SoftwareEpicV1, "Software epic", WorkItemKinds.Epic,
            [WorkBoardProfileKeys.SoftwareDeliveryV1], [], ProviderKey, []),
        new(WorkItemTypeKeys.SoftwareStoryV1, "Software story", WorkItemKinds.Story,
            [WorkBoardProfileKeys.SoftwareDeliveryV1], [WorkItemTypeKeys.SoftwareEpicV1], ProviderKey,
            [WorkItemApprovalPolicyKeys.SoftwareArchitectureReviewV1]),
        new(WorkItemTypeKeys.SoftwareTaskV1, "Software task", WorkItemKinds.Task,
            [WorkBoardProfileKeys.SoftwareDeliveryV1], [WorkItemTypeKeys.SoftwareStoryV1, WorkItemTypeKeys.SoftwareTaskV1], ProviderKey,
            [WorkItemApprovalPolicyKeys.SoftwareArchitectureReviewV1]),
        new(WorkItemTypeKeys.VideoGameEpicV1, "Game epic", WorkItemKinds.Epic,
            [WorkBoardProfileKeys.VideoGameProductionV1], [], ProviderKey, []),
        new(WorkItemTypeKeys.VideoGameStoryV1, "Game story", WorkItemKinds.Story,
            [WorkBoardProfileKeys.VideoGameProductionV1], [WorkItemTypeKeys.VideoGameEpicV1], ProviderKey, []),
        new(WorkItemTypeKeys.VideoGameTaskV1, "Game production task", WorkItemKinds.Task,
            [WorkBoardProfileKeys.VideoGameProductionV1], [WorkItemTypeKeys.VideoGameStoryV1, WorkItemTypeKeys.VideoGameTaskV1], ProviderKey, [])
    ];

    private static readonly IReadOnlyList<WorkBoardProfileDefinition> Profiles =
    [
        new(WorkBoardProfileKeys.GeneralWorkV1, "General work",
            [WorkItemTypeKeys.GeneralInitiativeV1, WorkItemTypeKeys.GeneralEpicV1,
             WorkItemTypeKeys.GeneralStoryV1, WorkItemTypeKeys.GeneralTaskV1], null),
        new(WorkBoardProfileKeys.SoftwareDeliveryV1, "Software delivery",
            [WorkItemTypeKeys.SoftwareEpicV1, WorkItemTypeKeys.SoftwareStoryV1,
             WorkItemTypeKeys.SoftwareTaskV1], "software-delivery.v1"),
        new(WorkBoardProfileKeys.VideoGameProductionV1, "Video-game production",
            [WorkItemTypeKeys.VideoGameEpicV1, WorkItemTypeKeys.VideoGameStoryV1,
             WorkItemTypeKeys.VideoGameTaskV1], "software-delivery.v1")
    ];

    private static readonly IReadOnlyDictionary<string, WorkItemTypeDefinition> TypesByKey =
        Types.ToDictionary(x => x.Key, StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, WorkBoardProfileDefinition> ProfilesByKey =
        Profiles.ToDictionary(x => x.Key, StringComparer.Ordinal);

    public static WorkItemTypeCatalog Read(string? profileKey = null)
    {
        if (string.IsNullOrWhiteSpace(profileKey))
            return new(Revision, Profiles, Types, Policies);
        var profile = RequireProfile(profileKey);
        return new(Revision, [profile], Types.Where(x =>
            x.CompatibleBoardProfiles.Contains(profile.Key, StringComparer.Ordinal)).ToList(), Policies);
    }

    public static WorkBoardProfileDefinition RequireProfile(string profileKey) =>
        ProfilesByKey.TryGetValue(profileKey, out var profile)
            ? profile
            : throw new ArgumentException($"Unknown work board profile '{profileKey}'.");

    public static WorkItemTypeDefinition RequireType(
        string profileKey,
        string typeKey,
        string? parentTypeKey)
    {
        var profile = RequireProfile(profileKey);
        if (!TypesByKey.TryGetValue(typeKey, out var type))
            throw new ArgumentException($"Unknown work item type '{typeKey}'.");
        if (!profile.PermittedTypeKeys.Contains(typeKey, StringComparer.Ordinal) ||
            !type.CompatibleBoardProfiles.Contains(profileKey, StringComparer.Ordinal))
            throw new ArgumentException($"Work item type '{typeKey}' is not permitted on board profile '{profileKey}'.");
        if (parentTypeKey is null)
        {
            if (type.Kind is not (WorkItemKinds.Initiative or WorkItemKinds.Epic) &&
                type.PermittedParentTypeKeys.Count > 0)
                throw new ArgumentException($"Work item type '{typeKey}' requires a compatible parent.");
        }
        else if (!type.PermittedParentTypeKeys.Contains(parentTypeKey, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Work item type '{typeKey}' cannot be parented by '{parentTypeKey}'.");
        }
        return type;
    }

    public static string DefaultTypeKey(string profileKey, string kind) =>
        Types.SingleOrDefault(x => x.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) &&
            x.CompatibleBoardProfiles.Contains(profileKey, StringComparer.Ordinal))?.Key
        ?? throw new ArgumentException($"Board profile '{profileKey}' does not support work kind '{kind}'.");
}
