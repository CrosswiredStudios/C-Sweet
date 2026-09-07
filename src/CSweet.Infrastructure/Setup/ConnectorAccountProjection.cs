using System.Text.Json;
using CSweet.Contracts.Plugins;

namespace CSweet.Infrastructure.Setup;

public sealed record ConnectorAccountChoice(string Id, string Name, string? Handle);

/// <summary>Projects provider data into bounded native labels, never markup or executable expressions.</summary>
public static class ConnectorAccountProjection
{
    public static IReadOnlyList<ConnectorAccountChoice> Read(JsonElement value, ConnectorAccountOptions options)
    {
        _ = ConnectorRequestMaterializer.Canonical(value);
        if (options.NextPageTokenPointer is { } page && ConnectorRequestMaterializer.At(value, page) is { } next &&
            next.ValueKind != JsonValueKind.Null && (next.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(next.GetString())))
            throw new InvalidOperationException("Account discovery must finish every page before selection.");
        var items = ConnectorRequestMaterializer.At(value, options.ItemsPointer);
        if (items is not { ValueKind: JsonValueKind.Array } || items.Value.GetArrayLength() > 200)
            throw new InvalidOperationException("The provider returned an invalid or oversized account list.");
        var result = new List<ConnectorAccountChoice>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.Value.EnumerateArray())
        {
            var id = Text(item, options.IdPointer, required: true)!;
            if (!ids.Add(id)) throw new InvalidOperationException("The provider returned ambiguous account identities.");
            result.Add(new(id, Text(item, options.NamePointer, required: true)!,
                options.HandlePointer is { } handle ? Text(item, handle, required: false) : null));
        }
        return result;
    }

    private static string? Text(JsonElement value, string pointer, bool required)
    {
        var node = ConnectorRequestMaterializer.At(value, pointer);
        if (!required && (node is null || node.Value.ValueKind == JsonValueKind.Null)) return null;
        if (node is not { ValueKind: JsonValueKind.String } || node.Value.GetString() is not { } text ||
            string.IsNullOrWhiteSpace(text) || text.Length > 256 || text.Any(char.IsControl))
            throw new InvalidOperationException("The provider returned an invalid account field.");
        return text;
    }
}
