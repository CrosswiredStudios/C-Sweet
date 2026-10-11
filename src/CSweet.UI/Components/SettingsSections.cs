using MudBlazor;

namespace CSweet.UI.Components;

public sealed record SettingsSection(string Group, string Title, string Url, string Description, string Icon);

public static class SettingsSections
{
    public static IReadOnlyList<SettingsSection> All { get; } = [
        new("Preferences", "Appearance", "/settings/appearance", "Choose the colors and atmosphere of your workspace.", Icons.Material.Outlined.Palette),
        new("Models", "LLM providers", "/settings/llm-providers", "Connect language models, check capabilities, and inspect usage.", Icons.Material.Outlined.AutoAwesome),
        new("Models", "GenAI providers", "/settings/genai-providers", "Configure image and video generation providers.", Icons.Material.Outlined.PermMedia),
        new("Agents", "Installed agents", "/settings/agents", "Manage agent definitions, updates, and defaults for new hires.", Icons.Material.Outlined.SmartToy),
        new("Agents", "Runtime", "/settings/agents/runtime", "Set execution limits, schedules, and cleanup policies.", Icons.Material.Outlined.Memory),
        new("Platform", "Offices", "/settings/offices", "Manage computers, capacity, and maintenance.", Icons.Material.Outlined.Hub),
        new("Platform", "Source control", "/settings/source-control", "Connect GitHub and inspect repository storage.", Icons.Material.Outlined.AccountTree),
        new("Platform", "Plugins", "/settings/plugins", "Review packages, connection profiles, and access grants.", Icons.Material.Outlined.Extension),
        new("Platform", "Communications", "/settings/communications", "Connect communication apps for your selected company.", Icons.Material.Outlined.Forum),
        new("Account", "Security", "/settings/security", "Manage account recovery and email delivery.", Icons.Material.Outlined.Security)
    ];
}
