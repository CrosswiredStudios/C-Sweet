using MudBlazor;

namespace CSweet.UI.Components;

/// <summary>A settings page entry. Titles and descriptions are resource keys in the Settings area.</summary>
public sealed record SettingsSection(string GroupKey, string TitleKey, string Url, string DescriptionKey, string Icon);

public static class SettingsSections
{
    public static IReadOnlyList<SettingsSection> All { get; } = [
        new("Settings.Group.Preferences", "Settings.Section.Appearance.Title", "/settings/appearance", "Settings.Section.Appearance.Description", Icons.Material.Outlined.Palette),
        new("Settings.Group.Preferences", "Settings.Section.Language.Title", "/settings/language", "Settings.Section.Language.Description", Icons.Material.Outlined.Language),
        new("Settings.Group.Models", "Settings.Section.LlmProviders.Title", "/settings/llm-providers", "Settings.Section.LlmProviders.Description", Icons.Material.Outlined.AutoAwesome),
        new("Settings.Group.Models", "Settings.Section.GenAiProviders.Title", "/settings/genai-providers", "Settings.Section.GenAiProviders.Description", Icons.Material.Outlined.PermMedia),
        new("Settings.Group.Agents", "Settings.Section.InstalledAgents.Title", "/settings/agents", "Settings.Section.InstalledAgents.Description", Icons.Material.Outlined.SmartToy),
        new("Settings.Group.Agents", "Settings.Section.Runtime.Title", "/settings/agents/runtime", "Settings.Section.Runtime.Description", Icons.Material.Outlined.Memory),
        new("Settings.Group.Platform", "Settings.Section.Offices.Title", "/settings/offices", "Settings.Section.Offices.Description", Icons.Material.Outlined.Hub),
        new("Settings.Group.Platform", "Settings.Section.SourceControl.Title", "/settings/source-control", "Settings.Section.SourceControl.Description", Icons.Material.Outlined.AccountTree),
        new("Settings.Group.Platform", "Settings.Section.Plugins.Title", "/settings/plugins", "Settings.Section.Plugins.Description", Icons.Material.Outlined.Extension),
        new("Settings.Group.Platform", "Settings.Section.ToolchainSetup.Title", "/settings/toolchains", "Settings.Section.ToolchainSetup.Description", Icons.Material.Outlined.Build),
        new("Settings.Group.Platform", "Settings.Section.Communications.Title", "/settings/communications", "Settings.Section.Communications.Description", Icons.Material.Outlined.Forum),
        new("Settings.Group.Account", "Settings.Section.Security.Title", "/settings/security", "Settings.Section.Security.Description", Icons.Material.Outlined.Security)
    ];
}
