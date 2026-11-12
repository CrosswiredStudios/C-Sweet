using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;

namespace CSweet.UnitTests;

/// <summary>
/// A tool's output schema must describe what its handler really returns. QFN Games, 2026-11-11:
/// <c>request_project_resource</c> was declared as an array while it returns one request, so every
/// successful request failed the gateway check after it was saved.
/// </summary>
public sealed class McpToolOutputContractTests
{
    [Fact]
    public void Resource_request_and_decision_return_one_request_and_read_returns_a_list()
    {
        var tools = McpToolCatalog.Registered.ToDictionary(x => x.Capability, StringComparer.Ordinal);
        Assert.Equal("object", Kind(tools[ResourceCapabilities.Request]));
        Assert.Equal("object", Kind(tools[ResourceCapabilities.Decide]));
        Assert.Equal("array", Kind(tools[ResourceCapabilities.Read]));
    }

    [Fact]
    public void Artifact_read_declares_one_document_or_a_list()
    {
        // Found by the SDK cross-check below: listing artifacts was declared as one object, so the
        // gateway rejected every successful list.
        var tools = McpToolCatalog.Registered.ToDictionary(x => x.Capability, StringComparer.Ordinal);
        Assert.Equal("object|array", Kind(tools[PlatformCapabilities.ArtifactRead]));
    }

    [Fact]
    public void Every_output_schema_has_the_root_kind_of_its_declared_result_type()
    {
        foreach (var tool in McpToolCatalog.Registered.Where(x => x.OutputSchema is not null))
        {
            if (!McpToolOutputContracts.Types.TryGetValue(tool.Capability, out var type)) continue;
            var expected = McpToolOutputContracts.IsObjectOrList(type) ? "object|array"
                : McpToolOutputContracts.IsCollection(type) ? "array" : "object";
            Assert.True(expected == Kind(tool), $"{tool.Capability} returns {type.Name} but declares a {Kind(tool)} output.");
        }
    }

    [Fact]
    public void Every_declared_result_type_belongs_to_a_registered_tool()
    {
        var registered = McpToolCatalog.Registered.Select(x => x.Capability).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(McpToolOutputContracts.Types.Keys.Where(x => !registered.Contains(x)));
    }

    /// <summary>
    /// Calls every typed SDK client method against a runtime that answers each tool with an empty
    /// value of the gateway's declared kind. A method whose result type has the other root kind
    /// fails to read it, which means the gateway declaration and the agent contract disagree.
    /// </summary>
    [Fact]
    public async Task Sdk_typed_clients_can_read_the_output_kind_the_gateway_declares()
    {
        string? invoked = null;
        var runtime = new AgentTestRuntime();
        foreach (var tool in McpToolCatalog.Registered)
        {
            var sample = Kind(tool) == "array" ? Parse("[]") : Parse("{}");
            var capability = tool.Capability;
            runtime.RegisterCapability<JsonElement, JsonElement>(capability, (_, _) =>
            {
                invoked = capability;
                return Task.FromResult(sample);
            });
        }
        var platform = runtime.CreateContext().Platform;
        var mismatches = new List<string>();
        var exercised = new HashSet<string>(StringComparer.Ordinal);
        foreach (var client in Clients(platform))
        {
            foreach (var method in client.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(x => !x.IsGenericMethodDefinition && x.ReturnType.IsGenericType &&
                                     x.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)))
            {
                invoked = null;
                Exception? failure = null;
                try
                {
                    var call = (Task)method.Invoke(client, method.GetParameters().Select(x => Argument(x.ParameterType)).ToArray())!;
                    var completed = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(2)));
                    if (completed == call) await call;
                }
                catch (Exception error) { failure = Unwrap(error); }
                if (invoked is null) continue;
                exercised.Add(invoked);
                // A tool whose request selects one record or a list accepts both root kinds.
                if (IsRootKindMismatch(failure) && !Kind(McpToolCatalog.Registered.Single(x => x.Capability == invoked)).Contains('|'))
                    mismatches.Add($"{client.GetType().Name}.{method.Name} reads {invoked} as {method.ReturnType.GetGenericArguments()[0].Name}, " +
                                   $"but the gateway declares a {Kind(McpToolCatalog.Registered.Single(x => x.Capability == invoked))} output.");
            }
        }
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches.Distinct()));
        Assert.Contains(ResourceCapabilities.Request, exercised);
    }

    [Fact]
    public void A_successful_result_that_breaks_its_schema_is_returned_marked_non_retryable()
    {
        var schema = Parse("""{"type":"array","items":{"type":"object"}}""");
        var saved = Parse("""{"id":"3a7d","status":"AwaitingApproval"}""");
        var violation = McpGatewayEndpoints.FindOutputContractViolation(true, saved, schema);
        Assert.NotNull(violation);
        Assert.Null(McpGatewayEndpoints.FindOutputContractViolation(false, saved, schema));
        Assert.Null(McpGatewayEndpoints.FindOutputContractViolation(true, Parse("[{}]"), schema));

        var meta = JsonSerializer.SerializeToElement(McpGatewayEndpoints.ResultMetadata(
            new CapabilityResult { RequestId = "r", Succeeded = true }, JsonNode.Parse(saved.GetRawText()), violation));
        var marker = meta.GetProperty("csweet").GetProperty("contractViolation");
        Assert.Equal(McpGatewayEndpoints.ContractViolationCode, marker.GetProperty("code").GetString());
        Assert.False(marker.GetProperty("retryable").GetBoolean());
        Assert.Null(McpGatewayEndpoints.ResultMetadata(new CapabilityResult { RequestId = "r", Succeeded = true }, null, null));
    }

    [Fact]
    public void A_failure_keeps_its_code_and_retry_hint()
    {
        var meta = JsonSerializer.SerializeToElement(McpGatewayEndpoints.ResultMetadata(
            new CapabilityResult { RequestId = "r", Succeeded = false, FailureCode = "platform.capability.denied", Retryable = true }, null, null));
        Assert.Equal("platform.capability.denied", meta.GetProperty("csweet").GetProperty("failureCode").GetString());
        Assert.True(meta.GetProperty("csweet").GetProperty("retryable").GetBoolean());
    }

    // A tool with no output schema is not validated, so it accepts any root kind.
    private static string Kind(McpToolDescriptor tool) =>
        tool.OutputSchema is not { } schema ? "object|array" :
        !schema.TryGetProperty("type", out var type) ? "object" :
        type.ValueKind == JsonValueKind.Array ? string.Join('|', type.EnumerateArray().Select(x => x.GetString())) :
        type.GetString()!;

    private static IEnumerable<object> Clients(PlatformCapabilityClient platform)
    {
        var sdk = typeof(PlatformCapabilityClient).Assembly;
        yield return platform;
        foreach (var property in typeof(PlatformCapabilityClient).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(x => x.PropertyType.Assembly == sdk && x.PropertyType.IsClass && x.PropertyType != typeof(PlatformCapabilityClient)))
            if (property.GetValue(platform) is { } client) yield return client;
        foreach (var type in sdk.GetExportedTypes().Where(x => x.IsClass && !x.IsAbstract))
        {
            var constructor = type.GetConstructor([typeof(PlatformCapabilityClient)]);
            if (constructor is not null) yield return constructor.Invoke([platform]);
        }
    }

    private static object? Argument(Type type)
    {
        if (type == typeof(CancellationToken)) return CancellationToken.None;
        if (type == typeof(string)) return "contract-test";
        if (type == typeof(Guid)) return Guid.NewGuid();
        if (type == typeof(JsonElement)) return Parse("{}");
        if (Nullable.GetUnderlyingType(type) is not null) return null;
        if (type.IsEnum) return Enum.GetValues(type).GetValue(0);
        if (type.IsValueType) return Activator.CreateInstance(type);
        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);
        if (type.IsGenericType && type.GetGenericArguments().Length == 1 &&
            type.IsAssignableFrom(type.GetGenericArguments()[0].MakeArrayType()))
            return Array.CreateInstance(type.GetGenericArguments()[0], 0);
        if (type.IsInterface || type.IsAbstract || typeof(Delegate).IsAssignableFrom(type)) return null;
        if (type.GetConstructor(Type.EmptyTypes) is { } empty) return empty.Invoke(null);
        return RuntimeHelpers.GetUninitializedObject(type);
    }

    private static Exception Unwrap(Exception error) =>
        error is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) :
        error is AggregateException { InnerException: { } aggregate } ? Unwrap(aggregate) : error;

    private static bool IsRootKindMismatch(Exception? failure)
    {
        for (var error = failure; error is not null; error = error.InnerException)
            if (error is JsonException json && json.Message.Contains("Path: $ |", StringComparison.Ordinal))
                return true;
        return false;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
