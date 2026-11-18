using System.Text.Json;
using CSweet.Contracts.Plugins;

namespace CSweet.UI.Services;

internal static class AgentConfigurationDefaults
{
    internal static object? Read(PluginConfigurationField field) => field.DefaultValue switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString(),
        { ValueKind: JsonValueKind.Number } value when value.TryGetDecimal(out var number) => number,
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => null
    };
}
