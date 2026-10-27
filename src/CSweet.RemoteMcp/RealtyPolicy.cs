using System.Text.Json;
namespace CSweet.RemoteMcp;

public static class RealtyPolicy
{
    public const string Endpoint = "https://realtyleads.ai/mcp";
    public const string Search = "realtyleads.inheritance.search.v1";
    public const string Lookup = "realtyleads.inheritance.lookup.v1";
    public const string Protocol = "2025-03-26";
    public static string RemoteName(string capability) => capability switch
    {
        Search => "search_inheritance_leads", Lookup => "get_inheritance_lead_by_id", _ => throw new UnauthorizedAccessException("Undeclared capability.")
    };
    public static void ValidateInput(string capability, JsonElement input)
    {
        _ = RemoteName(capability);
        if (input.ValueKind != JsonValueKind.Object || input.GetRawText().Length > 4096) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in input.EnumerateObject())
        {
            if (!names.Add(p.Name)) throw Invalid();
            if (capability == Lookup)
            {
                if (p.Name != "id" || p.Value.ValueKind != JsonValueKind.String || !Guid.TryParseExact(p.Value.GetString(), "D", out _)) throw Invalid();
            }
            else if (p.Name is "state" or "county" or "city" or "status")
            {
                if (p.Value.ValueKind != JsonValueKind.Null && (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString()!.Length > 128 || p.Value.GetString()!.Any(char.IsControl))) throw Invalid();
            }
            else if (p.Name == "limit")
            {
                if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var limit) || limit is < 1 or > 100) throw Invalid();
            }
            else if (p.Name == "minScore")
            {
                if (p.Value.ValueKind != JsonValueKind.Null && (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetDouble(out var score) || !double.IsFinite(score) || score is < 0 or > 100)) throw Invalid();
            }
            else throw Invalid();
        }
        if (capability == Lookup && !names.Contains("id")) throw Invalid();
    }
    private static ArgumentException Invalid() => new("The lead query does not match the reviewed input schema.");
}
