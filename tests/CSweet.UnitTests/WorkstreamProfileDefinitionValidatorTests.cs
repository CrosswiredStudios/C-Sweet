using System.Text;
using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.WorkManagement;

namespace CSweet.UnitTests;

public sealed class WorkstreamProfileDefinitionValidatorTests
{
    [Theory]
    [InlineData("video-game-production.v2", "workingTitle")]
    [InlineData("marketing-campaign-delivery.v1", "campaignName")]
    public void UnrelatedProfilesUseTheSameBoundedValidator(string key, string metadataField)
    {
        var definition = Definition(key, metadataField);
        var validated = WorkstreamProfileDefinitionValidator.Validate(
            Contribution(key, $"profiles/{key}.json"),
            Encoding.UTF8.GetBytes(definition));

        Assert.Equal(key, validated.Key);
        Assert.Equal(64, validated.Digest.Length);
        using var schema = JsonDocument.Parse(validated.MetadataSchemaJson);
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(
            new Dictionary<string, string> { [metadataField] = "Inspectable outcome" }));
        WorkstreamProfileDefinitionValidator.ValidateProfileData(schema.RootElement, data.RootElement);
    }

    [Fact]
    public void DomainMetadataIsRejectedBySchemaWithoutDomainCode()
    {
        var validated = WorkstreamProfileDefinitionValidator.Validate(
            Contribution("marketing-campaign-delivery.v1", "profile.json"),
            Encoding.UTF8.GetBytes(Definition("marketing-campaign-delivery.v1", "campaignName")));
        using var schema = JsonDocument.Parse(validated.MetadataSchemaJson);
        using var data = JsonDocument.Parse("""{"workingTitle":"Wrong domain"}""");

        var error = Assert.Throws<ArgumentException>(() =>
            WorkstreamProfileDefinitionValidator.ValidateProfileData(schema.RootElement, data.RootElement));

        Assert.Contains("campaignName is required", error.Message);
        Assert.Contains("workingTitle is not allowed", error.Message);
    }

    [Fact]
    public void DefinitionDigestBindsEveryDeclarativeChange()
    {
        var contribution = Contribution("example.delivery.v1", "profile.json");
        var first = WorkstreamProfileDefinitionValidator.Validate(
            contribution, Encoding.UTF8.GetBytes(Definition("example.delivery.v1", "brief")));
        var second = WorkstreamProfileDefinitionValidator.Validate(
            contribution, Encoding.UTF8.GetBytes(Definition("example.delivery.v1", "charter")));

        Assert.NotEqual(first.Digest, second.Digest);
    }

    [Fact]
    public void LifecycleTransitionsCannotReferenceUndeclaredStages()
    {
        var invalid = Definition("example.delivery.v1", "brief")
            .Replace("\"to\":\"done\"", "\"to\":\"undeclared\"", StringComparison.Ordinal);

        var error = Assert.Throws<ArgumentException>(() =>
            WorkstreamProfileDefinitionValidator.Validate(
                Contribution("example.delivery.v1", "profile.json"),
                Encoding.UTF8.GetBytes(invalid)));

        Assert.Contains("declared stages", error.Message);
    }

    [Fact]
    public void BoundedConditionalStaffingPredicatesAreValidatedAndEvaluatedGenerically()
    {
        using var profileData = JsonDocument.Parse("""{"online":true,"targets":["desktop","console"]}""");
        using var expectedOnline = JsonDocument.Parse("true");
        using var expectedTargets = JsonDocument.Parse("""["console","mobile"]""");

        Assert.True(BoundedJsonPredicateEvaluator.Evaluate(
            profileData.RootElement, "$.online", "equals", expectedOnline.RootElement));
        Assert.True(BoundedJsonPredicateEvaluator.Evaluate(
            profileData.RootElement, "$.targets", "contains-any", expectedTargets.RootElement));
        Assert.Throws<ArgumentException>(() => BoundedJsonPredicateEvaluator.Validate(
            "$.targets[*]", "contains-any", expectedTargets.RootElement));
        Assert.Throws<ArgumentException>(() => BoundedJsonPredicateEvaluator.Validate(
            "$.targets", "execute-plugin", expectedTargets.RootElement));
    }

    private static string Definition(string key, string metadataField) => $$$"""
        {
          "key":"{{{key}}}",
          "version":1,
          "displayName":"Example profile",
          "lifecyclePolicyKey":"example.lifecycle.v1",
          "defaultBoardProfileKey":"example.board.v1",
          "metadataSchema":{
            "type":"object",
            "additionalProperties":false,
            "required":["{{{metadataField}}}"],
            "properties":{"{{{metadataField}}}":{"type":"string"}}
          },
          "lifecycle":{
            "stages":[{"key":"intake"},{"key":"done"}],
            "transitions":[{"from":"intake","to":"done"}]
          },
          "workItemTypes":[
            {"key":"example.task.v1","displayName":"Task","kind":"Task","permittedParentTypeKeys":[],"requiredApprovalPolicyKeys":[]}
          ]
        }
        """;

    private static PluginWorkstreamProfileContribution Contribution(string key, string resource) => new()
    {
        Key = key,
        Version = 1,
        DefinitionResource = resource
    };
}
