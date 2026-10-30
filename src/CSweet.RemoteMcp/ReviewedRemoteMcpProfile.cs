using System.Text.Json;
using CSweet.Agent.Contracts.Packaging;
using CSweet.Agent.SDK;

namespace CSweet.RemoteMcp;

/// <summary>One reviewed capability-to-remote-tool mapping with the schemas the broker enforces.</summary>
public sealed record RemoteMcpToolPolicy(string Capability, string RemoteTool, string Description, JsonElement InputSchema, JsonElement OutputSchema);

/// <summary>
/// Trusted dispatch policy derived from an installed provider's reviewed manifest. Nothing here comes from agent
/// arguments; callers build it from the package version whose digest the consumer binding pins.
/// </summary>
public sealed class ReviewedRemoteMcpProfile
{
    public const int MaximumInputBytes = 4096;
    // Must bind exactly like the SDK review loader: case-sensitive names and no duplicates, so the endpoint a human
    // reviewed is the endpoint that receives the key.
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false
    };
    private readonly IReadOnlyDictionary<string, RemoteMcpToolPolicy> tools;

    private ReviewedRemoteMcpProfile(string providerName, string profile, string endpoint, string host, string header, bool bearer,
        string protocol, IReadOnlyDictionary<string, RemoteMcpToolPolicy> tools)
    {
        ProviderName = providerName; Profile = profile; Endpoint = endpoint; Host = host; Header = header; Bearer = bearer;
        Protocol = protocol; this.tools = tools;
    }

    public string ProviderName { get; }
    public string Profile { get; }
    public string Endpoint { get; }
    /// <summary>The only DNS host the broker may connect to, on port 443, after public-IPv4 pinning.</summary>
    public string Host { get; }
    public string Header { get; }
    public bool Bearer { get; }
    public string Protocol { get; }
    public IReadOnlyCollection<RemoteMcpToolPolicy> Tools => tools.Values.ToArray();
    public IReadOnlyCollection<string> Capabilities => tools.Keys.ToArray();

    /// <summary>Parses and re-validates a reviewed manifest. Throws <see cref="InvalidOperationException"/> when it is not a valid remote MCP declaration.</summary>
    public static ReviewedRemoteMcpProfile FromManifestJson(string manifestJson)
    {
        AgentManifest manifest;
        try
        {
            using (var document = JsonDocument.Parse(manifestJson))
                if (HasAmbiguousPropertyNames(document.RootElement)) throw Invalid();
            manifest = JsonSerializer.Deserialize<AgentManifest>(manifestJson, Strict) ?? throw Invalid();
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException) { throw Invalid(); }
        return FromManifest(manifest);
    }

    public static ReviewedRemoteMcpProfile FromManifest(AgentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        try { return Build(manifest); }
        catch (Exception e) when (e is not OperationCanceledException) { throw Invalid(); }
    }

    /// <summary>
    /// True when any object repeats a property name ignoring case. Host readers that bind case-insensitively would
    /// otherwise see a different value than the case-sensitive review loader (for example a second "Endpoint").
    /// </summary>
    public static bool HasAmbiguousPropertyNames(JsonElement element, int depth = 0)
    {
        if (depth > 64) return true;
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(item => HasAmbiguousPropertyNames(item, depth + 1));
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name) || HasAmbiguousPropertyNames(property.Value, depth + 1)) return true;
        return false;
    }

    private static ReviewedRemoteMcpProfile Build(AgentManifest manifest)
    {
        if (manifest.Kind != "remote-mcp" || ReviewedRemoteMcpValidator.Validate(manifest).Count != 0 || manifest.ReviewedRemoteMcp is not { } d)
            throw Invalid();
        var provided = manifest.Provides.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var mapped = d.Tools.ToDictionary(t => t.Capability, t =>
        {
            var capability = provided[t.Capability];
            return new RemoteMcpToolPolicy(t.Capability, t.RemoteTool, capability.Description,
                capability.InputSchema.Clone(), capability.OutputSchema.Clone());
        }, StringComparer.Ordinal);
        // The validator only admits protocol revisions this broker implements; negotiate the first declared one.
        return new(manifest.Name, d.Profile, d.Endpoint, ReviewedRemoteMcpValidator.EndpointHost(d.Endpoint), d.Header,
            d.Authentication == "bearer", d.ProtocolVersions[0], mapped);
    }

    /// <summary>Returns the reviewed mapping, or throws <see cref="UnauthorizedAccessException"/> for an undeclared capability.</summary>
    public RemoteMcpToolPolicy Tool(string capability) =>
        capability is not null && tools.TryGetValue(capability, out var tool) ? tool : throw new UnauthorizedAccessException("Undeclared capability.");

    public bool Declares(string capability) => capability is not null && tools.ContainsKey(capability);

    /// <summary>Validates agent arguments against the reviewed input schema. Throws <see cref="ArgumentException"/> without echoing values.</summary>
    public void ValidateInput(string capability, JsonElement input)
    {
        var tool = Tool(capability);
        if (input.ValueKind != JsonValueKind.Object || input.GetRawText().Length > MaximumInputBytes ||
            ClosedJsonSchema.Validate(tool.InputSchema, input) is not null)
            throw new ArgumentException("The request does not match the reviewed input schema.");
    }

    /// <summary>True when a provider result conforms to the reviewed output schema.</summary>
    public bool IsReviewedOutput(string capability, JsonElement output) =>
        ClosedJsonSchema.Validate(Tool(capability).OutputSchema, output, rejectControlCharacters: false) is null;

    private static InvalidOperationException Invalid() => new("The installed provider is not a valid reviewed remote MCP declaration.");
}
