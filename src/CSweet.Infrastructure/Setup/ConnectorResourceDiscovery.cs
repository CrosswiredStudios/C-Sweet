using System.Text.Json;
using CSweet.Contracts.Plugins;

namespace CSweet.Infrastructure.Setup;

/// <summary>Collects a complete bounded inventory before any grant or resource update. Contains no provider-specific API behavior.</summary>
public static class ConnectorResourceDiscovery
{
    public static async Task<IReadOnlyList<ConnectorAccountChoice>> CollectAsync(PluginAccountManagement declaration,
        PluginProviderOperationDeclaration operation, Func<JsonElement, CancellationToken, Task<JsonElement>> invoke,
        CancellationToken token)
    {
        if (operation.Capability != declaration.DiscoveryCapability || operation.Effect != "read" ||
            operation.Http is not { Bootstrap: true, Method: "GET" } http || http.Connection != declaration.Connection ||
            declaration.MaximumResources is < 1 or > 10000 ||
            (declaration.PageTokenInput is null) != (declaration.ResourceProjection.NextPageTokenPointer is null))
            throw new InvalidOperationException("A reviewed read-only resource discovery declaration is required.");
        var resources = new Dictionary<string, ConnectorAccountChoice>(StringComparer.Ordinal);
        var continuations = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;
        for (var page = 0; page < 1000; page++)
        {
            token.ThrowIfCancellationRequested();
            var arguments = new Dictionary<string, string>();
            if (pageToken is not null) arguments.Add(declaration.PageTokenInput!, pageToken);
            var input = JsonSerializer.SerializeToElement(arguments);
            RequestSchemaValidator.Validate(input, operation.InputSchema);
            var result = await invoke(input, token);
            token.ThrowIfCancellationRequested();
            _ = ConnectorRequestMaterializer.Canonical(result); // Duplicate property names are ambiguous, not provider evidence.
            RequestSchemaValidator.Validate(result, operation.OutputSchema);
            var rows = ConnectorAccountProjection.Read(result, declaration.ResourceProjection with { NextPageTokenPointer = null });
            foreach (var row in rows)
            {
                if (resources.TryGetValue(row.Id, out var previous) && previous != row)
                    throw new InvalidOperationException("Resource discovery returned conflicting identities across pages.");
                resources[row.Id] = row;
                if (resources.Count > declaration.MaximumResources)
                    throw new InvalidOperationException("Resource discovery exceeded the reviewed collection limit; no partial inventory was accepted.");
            }
            var continuation = declaration.ResourceProjection.NextPageTokenPointer is { } pointer
                ? ConnectorRequestMaterializer.At(result, pointer) : null;
            if (continuation is null || continuation.Value.ValueKind == JsonValueKind.Null ||
                continuation.Value.ValueKind == JsonValueKind.String && continuation.Value.GetString() == string.Empty)
                return resources.Values.ToArray();
            if (continuation.Value.ValueKind != JsonValueKind.String || continuation.Value.GetString() is not { Length: > 0 and <= 2048 } next ||
                next.Any(char.IsControl) || !continuations.Add(next))
                throw new InvalidOperationException("Resource discovery returned an invalid or repeated continuation token.");
            pageToken = next;
        }
        throw new InvalidOperationException("Resource discovery exceeded its page budget; no partial inventory was accepted.");
    }
}
