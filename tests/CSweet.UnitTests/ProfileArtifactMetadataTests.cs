using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.WorkManagement;

namespace CSweet.UnitTests;

public sealed class ProfileArtifactMetadataTests
{
    private static JsonDocument Profile(string key = "publisher.campaign-brief.v1") => JsonDocument.Parse("""
        {"key":"campaign.v1","version":1,"displayName":"Campaign","metadataSchema":{"type":"object"},
         "lifecyclePolicyKey":"campaign.lifecycle","defaultBoardProfileKey":"campaign.board",
         "lifecycle":{"stages":[{"key":"draft"}]},
         "artifactTypes":[{"key":"__TYPE__","displayName":"Campaign brief","schemaVersion":"1.0",
         "payloadSchema":{"type":"object","required":["Audience"],"additionalProperties":false,
         "properties":{"Audience":{"type":"string"}}}}]}
        """.Replace("__TYPE__", key, StringComparison.Ordinal));

    [Theory]
    [InlineData("publisher.campaign-brief.v1")]
    [InlineData("publisher.construction-review.v1")]
    public void UnrelatedDomainsUseTheSameMetadataValidation(string type)
    {
        using var profile = Profile(type);
        var contribution = new PluginWorkstreamProfileContribution
        {
            Key = "campaign.v1", Version = 1, DefinitionResource = "profile.json"
        };
        var validated = WorkstreamProfileDefinitionValidator.Validate(contribution,
            Encoding.UTF8.GetBytes(profile.RootElement.GetRawText()));
        using var payload = JsonDocument.Parse("""{"Audience":"Review team"}""");
        ProfileArtifactMetadata.ValidatePayload(profile.RootElement, type, "1.0", payload.RootElement);
        Assert.Equal("Campaign brief", ProfileArtifactMetadata.Find(profile.RootElement, type)!.Value.GetProperty("displayName").GetString());
        Assert.Equal(64, validated.Digest.Length);
    }

    [Fact]
    public void RegisteredTypesRejectWrongVersionAndInvalidPayload()
    {
        using var profile = Profile();
        using var payload = JsonDocument.Parse("""{"Audience":42}""");
        Assert.Throws<ArgumentException>(() => ProfileArtifactMetadata.ValidatePayload(
            profile.RootElement, "publisher.campaign-brief.v1", "2.0", payload.RootElement));
        Assert.Throws<ArgumentException>(() => ProfileArtifactMetadata.ValidatePayload(
            profile.RootElement, "publisher.campaign-brief.v1", "1.0", payload.RootElement));
    }

    [Fact]
    public void UnregisteredTypesRetainExistingGenericEnvelopeCompatibility()
    {
        using var profile = Profile();
        using var payload = JsonDocument.Parse("{}");
        Assert.Null(ProfileArtifactMetadata.Find(profile.RootElement, "another-extension.report.v1"));
        ProfileArtifactMetadata.ValidatePayload(profile.RootElement, "another-extension.report.v1", "1.0", payload.RootElement);
    }

    [Fact]
    public async Task MetadataResolutionUsesOrganizationVersionAndDigest()
    {
        await using var db = new CSweet.Infrastructure.Persistence.CSweetDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<CSweet.Infrastructure.Persistence.CSweetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var organizationId = Guid.NewGuid();
        var workstream = new CSweet.Domain.Core.Workstream
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ProfileKey = "publisher.delivery",
            ProfileVersion = 1, ProfileDefinitionDigest = "pinned"
        };
        db.Workstreams.Add(workstream);
        db.WorkstreamProfileDefinitions.AddRange(
            new CSweet.Domain.Core.WorkstreamProfileDefinitionRecord
            {
                Id = Guid.NewGuid(), Key = "publisher.delivery", Version = 1,
                DefinitionDigest = "pinned", DefinitionJson = "{\"displayName\":\"Pinned\"}"
            },
            new CSweet.Domain.Core.WorkstreamProfileDefinitionRecord
            {
                Id = Guid.NewGuid(), Key = "publisher.delivery", Version = 2,
                DefinitionDigest = "newer", DefinitionJson = "{\"displayName\":\"Newer\"}"
            });
        await db.SaveChangesAsync();
        Assert.Equal("{\"displayName\":\"Pinned\"}", await ProfileArtifactMetadata.ReadDefinitionAsync(
            db, organizationId, workstream.Id, default));
        Assert.Null(await ProfileArtifactMetadata.ReadDefinitionAsync(db, Guid.NewGuid(), workstream.Id, default));
        workstream.ProfileDefinitionDigest = "mismatch";
        await db.SaveChangesAsync();
        Assert.Null(await ProfileArtifactMetadata.ReadDefinitionAsync(db, organizationId, workstream.Id, default));
    }
    [Theory]
    [InlineData("$ref", "\"https://example.invalid/schema.json\"")]
    [InlineData("required", "42")]
    [InlineData("additionalProperties", "{}")]
    public void UnsupportedOrMalformedSchemaRulesAreRejectedAtRegistration(string key, string value)
    {
        using var profile = Profile();
        var root = System.Text.Json.Nodes.JsonNode.Parse(profile.RootElement.GetRawText())!;
        root["artifactTypes"]![0]!["payloadSchema"]![key] = System.Text.Json.Nodes.JsonNode.Parse(value);
        Assert.Throws<ArgumentException>(() => WorkstreamProfileDefinitionValidator.Validate(
            new PluginWorkstreamProfileContribution { Key = "campaign.v1", Version = 1, DefinitionResource = "profile.json" },
            Encoding.UTF8.GetBytes(root.ToJsonString())));
    }
    [Fact]
    public void DuplicateTypeDeclarationsAreRejected()
    {
        using var profile = Profile();
        var root = System.Text.Json.Nodes.JsonNode.Parse(profile.RootElement.GetRawText())!;
        var types = root["artifactTypes"]!.AsArray();
        types.Add(types[0]!.DeepClone());
        Assert.Throws<ArgumentException>(() => WorkstreamProfileDefinitionValidator.Validate(
            new PluginWorkstreamProfileContribution { Key = "campaign.v1", Version = 1, DefinitionResource = "profile.json" },
            Encoding.UTF8.GetBytes(root.ToJsonString())));
    }
}
