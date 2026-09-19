using CSweet.Domain.Core;
namespace CSweet.Infrastructure.Core;

/// <summary>Built-in profile; installing a manager package is not a prerequisite for a prototype.</summary>
public static class SoftwarePrototypeProfile
{
    public const string Key = "software-prototype.v1";
    public const string Digest = "5377fbe74d83f7238ca4a05bdcb04f6df16c2d51d88620d6a42417a5864efd2c";
    public const string Definition = """
{"key":"software-prototype.v1","version":1,"displayName":"Software prototype","lifecyclePolicyKey":"software-prototype.lifecycle.v1","defaultBoardProfileKey":"general-work.v1","metadataSchema":{"type":"object","properties":{"intakeId":{"type":"string"},"setupChoiceMessageId":{"type":"string"},"participantIds":{"type":"array","items":{"type":"string"}}},"additionalProperties":false},"lifecycle":{"stages":[{"key":"Development"},{"key":"Testing"},{"key":"Completed"},{"key":"Cancelled"}],"transitions":[{"from":"Development","to":"Testing"},{"from":"Testing","to":"Development"},{"from":"Testing","to":"Completed"},{"from":"Development","to":"Cancelled"},{"from":"Testing","to":"Cancelled"},{"from":"Completed","to":"Development"},{"from":"Cancelled","to":"Development"}]}}
""";
    public static WorkstreamProfileDefinitionRecord Create() => new() {
        Id = Guid.Parse("01a5aeed-8b71-4b9d-8ac2-0a68b31720e5"), Key = Key, Version = 1, DisplayName = "Software prototype",
        MetadataSchemaJson = System.Text.Json.JsonDocument.Parse(Definition).RootElement.GetProperty("metadataSchema").GetRawText(),
        LifecyclePolicyKey = "software-prototype.lifecycle.v1", DefaultBoardProfileKey = "general-work.v1",
        ProviderPackageId = "com.csweet.platform", ProviderPackageVersion = "1.0.0", DefinitionDigest = Digest, DefinitionJson = Definition
    };
}
