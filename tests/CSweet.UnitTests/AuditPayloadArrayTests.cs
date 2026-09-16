using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class AuditPayloadArrayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Redacts_array_strings_without_invalidating_enumeration(bool capture)
    {
        const string json = """
            {"criteria":["Dockerfile exists","authorization: secret-value",null,42,
                ["bearer nested-token"],{"password":"hidden-password"},
                "{\"token\":\"embedded-secret\",\"checks\":[\"first\",\"second\"]}"]}
            """;
        var result = capture
            ? AuditPayloadSanitizer.Capture(Encoding.UTF8.GetBytes(json), "application/json").FullContent
            : AuditPayloadSanitizer.RedactJson(json);
        var array = JsonNode.Parse(result!)!["criteria"]!.AsArray();
        Assert.Equal(7, array.Count);
        Assert.Equal("Dockerfile exists", array[0]!.GetValue<string>());
        Assert.Equal("authorization: [REDACTED]", array[1]!.GetValue<string>());
        Assert.Null(array[2]);
        Assert.Equal(42, array[3]!.GetValue<int>());
        Assert.Equal("bearer [REDACTED]", array[4]![0]!.GetValue<string>());
        Assert.Equal("[REDACTED]", array[5]!["password"]!.GetValue<string>());
        var embedded = JsonNode.Parse(array[6]!.GetValue<string>())!;
        Assert.Equal("[REDACTED]", embedded["token"]!.GetValue<string>());
        Assert.Equal(2, embedded["checks"]!.AsArray().Count);
        Assert.DoesNotContain("secret-value", result);
        Assert.DoesNotContain("nested-token", result);
        Assert.DoesNotContain("hidden-password", result);
        Assert.DoesNotContain("embedded-secret", result);
    }

    [Fact]
    public void Redacts_serialized_planning_specification_and_preserves_acceptance_criteria()
    {
        var plan = JsonSerializer.Serialize(new { acceptanceCriteria = new[] { "Dockerfile exists", "No network installation" } });
        var evidence = JsonSerializer.SerializeToUtf8Bytes(new { source = new { PlanningSpecificationJson = plan, Status = "Ready" } });
        var captured = AuditPayloadSanitizer.Capture(evidence, "application/json");
        var saved = JsonNode.Parse(captured.FullContent!)!["source"]!;
        Assert.Equal("Ready", saved["Status"]!.GetValue<string>());
        var criteria = JsonNode.Parse(saved["PlanningSpecificationJson"]!.GetValue<string>())!["acceptanceCriteria"]!.AsArray();
        Assert.Equal(new[] { "Dockerfile exists", "No network installation" }, criteria.Select(x => x!.GetValue<string>()));
    }
}
