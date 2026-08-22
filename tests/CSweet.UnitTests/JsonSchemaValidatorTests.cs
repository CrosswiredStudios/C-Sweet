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

        Assert.Throws<InvalidOperationException>(() =>
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

    private static JsonElement Json(string value) =>
        JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 128 }).RootElement.Clone();
}
