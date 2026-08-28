using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;

namespace CSweet.UI.Services;

public static class AgentConfigurationVisibility
{
    public static bool IsVisible(
        PluginConfigurationField field,
        IReadOnlyDictionary<string, object?> values) =>
        IsVisible(field.VisibleWhenFieldKey, field.VisibleWhenValue, values);

    public static bool IsVisible(
        PluginConfigurationField field,
        IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(field.VisibleWhenFieldKey))
            return true;
        return values.TryGetValue(field.VisibleWhenFieldKey, out var value) &&
               string.Equals(value, field.VisibleWhenValue, StringComparison.Ordinal);
    }

    public static bool IsVisible(
        AgentConfigurationField field,
        IReadOnlyDictionary<string, object?> values) =>
        IsVisible(field.VisibleWhenFieldKey, field.VisibleWhenValue, values);

    private static bool IsVisible(
        string? controllerKey,
        string? expectedValue,
        IReadOnlyDictionary<string, object?> values)
    {
        if (string.IsNullOrWhiteSpace(controllerKey))
            return true;
        if (!values.TryGetValue(controllerKey, out var value) || value is null)
            return false;

        var text = value is JsonElement element
            ? element.ValueKind == JsonValueKind.String ? element.GetString() : null
            : value.ToString();
        return string.Equals(text, expectedValue, StringComparison.Ordinal);
    }
}
