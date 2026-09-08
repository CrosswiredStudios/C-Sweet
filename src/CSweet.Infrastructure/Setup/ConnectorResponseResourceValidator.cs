using System.Text.Json;

namespace CSweet.Infrastructure.Setup;

/// <summary>Provider-neutral ownership validation before response content reaches storage or a runtime.</summary>
public static class ConnectorResponseResourceValidator
{
    public static void Validate(byte[] body, ConnectorPreparedRequest request)
    {
        if (request.ResponseResourcePointers is not { Count: > 0 } pointers) return;
        if (string.IsNullOrWhiteSpace(request.BoundResourceId) || pointers.Count > 8)
            throw new UnauthorizedAccessException("The response has no valid confirmed resource binding.");
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        _ = ConnectorRequestMaterializer.Hash(document.RootElement); // Reject duplicate keys before inspecting ownership.
        foreach (var pointer in pointers)
        {
            if (string.IsNullOrEmpty(pointer) || pointer.Length > 256 ||
                !System.Text.RegularExpressions.Regex.IsMatch(pointer, @"\A(?:/(?:[A-Za-z0-9_-]+|\*))+\z") || pointer.Count(c => c == '*') > 1)
                throw new UnauthorizedAccessException("Invalid response resource binding.");
            Check(document.RootElement, pointer.Split('/').Skip(1).ToArray(), 0, request.BoundResourceId);
        }
    }

    private static void Check(JsonElement element, string[] path, int index, string expected)
    {
        if (index == path.Length)
        {
            if (element.ValueKind != JsonValueKind.String || element.GetString() != expected) Deny();
            return;
        }
        var segment = path[index];
        if (segment == "*")
        {
            if (element.ValueKind != JsonValueKind.Array) Deny();
            foreach (var item in element.EnumerateArray()) Check(item, path, index + 1, expected);
            return; // An existing empty array is valid; a missing array is not.
        }
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(segment, out var child))
            Check(child, path, index + 1, expected);
        else if (element.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var offset) && offset >= 0 && offset < element.GetArrayLength())
            Check(element[offset], path, index + 1, expected);
        else Deny();
    }
    private static void Deny() => throw new UnauthorizedAccessException("The provider response does not match the confirmed account; its content was withheld.");
}
