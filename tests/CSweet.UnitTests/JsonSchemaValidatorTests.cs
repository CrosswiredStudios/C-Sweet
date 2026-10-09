using System.Text.Json;
using CSweet.AgentHost.Broker;

namespace CSweet.UnitTests;

public sealed class JsonSchemaValidatorTests
{
    [Fact]
    public void Validate_EnforcesNestedTypesFormatsBoundsAndAdditionalProperties()
    {
        var schema = Json("""
            {
              "type":"object",
              "required":["id","items"],
              "properties":{
                "id":{"type":"string","format":"uuid"},
                "items":{"type":"array","minItems":1,"maxItems":2,"items":{
                  "type":"object",
                  "required":["name","score"],
                  "properties":{
                    "name":{"type":"string","minLength":2,"maxLength":8},
                    "score":{"type":"number","minimum":0,"maximum":10}
                  },
                  "additionalProperties":false
                }}
              },
              "additionalProperties":false
            }
            """);
        var valid = Json("""{"id":"11111111-1111-1111-1111-111111111111","items":[{"name":"safe","score":8}]}""");

        JsonSchemaValidator.Validate(valid, schema);
    }

    [Theory]
    [InlineData("""{"id":"not-a-guid","items":[{"name":"safe","score":8}]}""")]
    [InlineData("""{"id":"11111111-1111-1111-1111-111111111111","items":[]}""")]
    [InlineData("""{"id":"11111111-1111-1111-1111-111111111111","items":[{"name":"x","score":8}]}""")]
    [InlineData("""{"id":"11111111-1111-1111-1111-111111111111","items":[{"name":"safe","score":11}]}""")]
    [InlineData("""{"id":"11111111-1111-1111-1111-111111111111","items":[{"name":"safe","score":8,"admin":true}]}""")]
    public void Validate_RejectsMalformedOrOutOfPolicyInput(string input)
    {
        var schema = Json("""
            {"type":"object","required":["id","items"],"properties":{
              "id":{"type":"string","format":"uuid"},
              "items":{"type":"array","minItems":1,"maxItems":2,"items":{
                "type":"object","required":["name","score"],"properties":{
                  "name":{"type":"string","minLength":2,"maxLength":8},
                  "score":{"type":"number","minimum":0,"maximum":10}
                },"additionalProperties":false}}
            },"additionalProperties":false}
            """);

        Assert.Throws<InvalidOperationException>(() =>
            JsonSchemaValidator.Validate(Json(input), schema));
    }

    [Fact]
    public void Validate_RejectsPayloadBeyondMaximumDepth()
    {
        var schemaText = """{"type":"number"}""";
        var valueText = "0";
        for (var index = 0; index < 34; index++)
        {
            schemaText = $$"""{"type":"object","properties":{"x":{{schemaText}}},"additionalProperties":false}""";
            valueText = $$"""{"x":{{valueText}}}""";
        }

        Assert.ThrowsAny<InvalidOperationException>(() =>
            JsonSchemaValidator.Validate(Json(valueText), Json(schemaText)));
    }

    [Fact]
    public void ValidateSchema_RejectsFeaturesTheRuntimeDoesNotEnforce()
    {
        var schema = Json("""{"type":"object","patternProperties":{"^x-":{"type":"string"}}}""");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            JsonSchemaValidator.ValidateSchema(schema));

        Assert.Contains("unsupported keyword", exception.Message);
    }

    [Fact]
    public void Validate_SupportsBoundedLocalDefinitionsUsedByProviderCapabilities()
    {
        var schema = Json("""
            {
              "type":"object",
              "required":["planHash","assignments","estimate"],
              "properties":{
                "planHash":{"type":"string","pattern":"^[a-f0-9]{4}$"},
                "assignments":{"type":"array","uniqueItems":true,"items":{"$ref":"#/$defs/assignment"}},
                "estimate":{"type":"number","exclusiveMinimum":0,"maximum":100}
              },
              "$defs":{
                "assignment":{
                  "type":"object",
                  "required":["id"],
                  "properties":{"id":{"type":"string","format":"uuid"}},
                  "additionalProperties":false
                }
              },
              "additionalProperties":false
            }
            """);
        var valid = Json("""
            {"planHash":"a1f0","assignments":[{"id":"11111111-1111-1111-1111-111111111111"}],"estimate":1}
            """);

        JsonSchemaValidator.ValidateSchema(schema);
        JsonSchemaValidator.Validate(valid, schema);
    }

    [Theory]
    [InlineData("""{"planHash":"INVALID","assignments":[],"estimate":1}""")]
    [InlineData("""{"planHash":"a1f0","assignments":[],"estimate":0}""")]
    [InlineData("""{"planHash":"a1f0","assignments":[{"id":"11111111-1111-1111-1111-111111111111"},{"id":"11111111-1111-1111-1111-111111111111"}],"estimate":1}""")]
    public void Validate_EnforcesProviderCapabilityDefinitionConstraints(string input)
    {
        var schema = Json("""
            {
              "type":"object",
              "required":["planHash","assignments","estimate"],
              "properties":{
                "planHash":{"type":"string","pattern":"^[a-f0-9]{4}$"},
                "assignments":{"type":"array","uniqueItems":true,"items":{"$ref":"#/$defs/assignment"}},
                "estimate":{"type":"number","exclusiveMinimum":0}
              },
              "$defs":{"assignment":{"type":"object","required":["id"],"properties":{"id":{"type":"string","format":"uuid"}},"additionalProperties":false}},
              "additionalProperties":false
            }
            """);

        Assert.Throws<InvalidOperationException>(() =>
            JsonSchemaValidator.Validate(Json(input), schema));
    }

    [Theory]
    [InlineData("""{"$ref":"https://example.com/schema"}""")]
    [InlineData("""{"$ref":"#/$defs/missing","$defs":{}}""")]
    public void ValidateSchema_RejectsExternalOrMissingReferences(string schemaText)
    {
        Assert.Throws<InvalidOperationException>(() =>
            JsonSchemaValidator.ValidateSchema(Json(schemaText)));
    }

    [Theory]
    [InlineData("3", true)]
    [InlineData("\"word\"", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("\"longer-than-five\"", false)]
    public void OneOfRequiresExactlyOneAlternativeAndEnforcesCommonConstraints(string value, bool valid)
    {
        var schema = Json("""{"minimum":1,"maxLength":5,"oneOf":[{"type":"number"},{"$ref":"#/$defs/text"}],"$defs":{"text":{"type":"string"}}}""");
        JsonSchemaValidator.ValidateSchema(schema);
        if (valid) JsonSchemaValidator.Validate(Json(value), schema);
        else Assert.Throws<InvalidOperationException>(() => JsonSchemaValidator.Validate(Json(value), schema));
    }

    [Fact]
    public void OneOfRejectsAmbiguousMatches()
    {
        var schema = Json("""{"oneOf":[{"type":"number"},{"type":"integer"}]}""");
        Assert.Throws<InvalidOperationException>(() => JsonSchemaValidator.Validate(Json("1"), schema));
        JsonSchemaValidator.Validate(Json("1.5"), schema);
    }

    [Theory]
    [InlineData("{\"oneOf\":[]}")]
    [InlineData("{\"oneOf\":{}}")]
    [InlineData("{\"oneOf\":[true]}")]
    [InlineData("{\"oneOf\":[{\"unsupported\":true}]}")]
    public void OneOfSchemaValidatesAlternativeShapeAndSupportedKeywords(string schema) =>
        Assert.Throws<InvalidOperationException>(() => JsonSchemaValidator.ValidateSchema(Json(schema)));

    [Fact]
    public void OneOfSchemaBoundsAlternativeCount()
    {
        var schema = JsonSerializer.SerializeToElement(new { oneOf = Enumerable.Range(0, 17).Select(_ => new { type = "string" }).ToArray() });
        Assert.Throws<InvalidOperationException>(() => JsonSchemaValidator.ValidateSchema(schema));
    }

    [Fact]
    public void OneOfCannotHideRecursiveDepthLimitBehindMatchingAlternative()
    {
        var schema = Json("""{"oneOf":[{"$ref":"#/$defs/recursive"},{"type":"number"}],"$defs":{"recursive":{"$ref":"#/$defs/recursive"}}}""");
        JsonSchemaValidator.ValidateSchema(schema);
        Assert.Contains("maximum validation depth", Assert.ThrowsAny<InvalidOperationException>(() => JsonSchemaValidator.Validate(Json("1"), schema)).Message);
    }

    [Fact]
    public void OneOfBoundsRepeatedBranchTraversalWithoutHidingWorkLimit()
    {
        // A compact definition graph would otherwise cause exponentially repeated work.
        var definitions = new Dictionary<string, object> { ["d0"] = new { type = "string" } };
        for (var i = 1; i <= 12; i++)
            definitions[$"d{i}"] = new { oneOf = Enumerable.Range(0, 3)
                .Select(_ => new Dictionary<string, string> { ["$ref"] = $"#/$defs/d{i - 1}" }).ToArray() };
        var schema = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["$ref"] = "#/$defs/d12", ["$defs"] = definitions });
        JsonSchemaValidator.ValidateSchema(schema);
        Assert.Contains("maximum validation work", Assert.ThrowsAny<InvalidOperationException>(() => JsonSchemaValidator.Validate(Json("1"), schema)).Message);
    }

    private static JsonElement Json(string value) =>
        JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 128 }).RootElement.Clone();
}
