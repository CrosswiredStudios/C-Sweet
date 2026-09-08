using System.Text.Json;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class GameDeliveryProfileHostTests
{
    [Fact]
    public void PackagedGameDeliveryPolicyPassesHostValidation()
    {
        using var stream = typeof(GameDeliveryProfileHostTests).Assembly.GetManifestResourceStream(
            "CSweet.UnitTests.Fixtures.video-game-production.v2.5.json")!;
        using var document = JsonDocument.Parse(stream);
        var validated = WorkstreamProfileDefinitionValidator.Validate(new CSweet.Contracts.Plugins.PluginWorkstreamProfileContribution
        {
            Key = "video-game-production.v2", Version = 5, DefinitionResource = "profiles/video-game-production.v2.5.json"
        }, System.Text.Encoding.UTF8.GetBytes(document.RootElement.GetRawText()));
        Assert.Equal(5, validated.Version);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var root = document.RootElement;
        var workflow = root.GetProperty("boardWorkflow").Deserialize<WorkBoardWorkflowTemplate>(options)!;
        var policy = root.GetProperty("orchestration").Deserialize<WorkOrchestrationProfileTemplate>(options)!;
        var columns = workflow.Columns.ToDictionary(x => x.Key, _ => Guid.NewGuid());
        var stages = policy.Stages.Select(x => new WorkOrchestrationStageDefinition(x.Key, x.Name, x.StageType,
            x.ColumnKey is null ? null : columns[x.ColumnKey], x.Instructions, x.InputSchemaJson, x.OutputSchemaJson,
            x.TimeoutSeconds, x.ConcurrencyLimit, x.RetryPolicy, x.PlatformAction, x.IsSuccessfulTerminal)).ToArray();
        var errors = WorkOrchestrationPolicyValidator.Validate(policy.InitialStageKey, policy.MergeMode,
            policy.Concurrency, stages, policy.Transitions, columns.Values.ToHashSet());
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(x => $"{x.Code}: {x.Message}")));
        Assert.Equal(GovernedMergeWorkActionExecutor.ActionName, Assert.Single(stages, x => x.Key == "governed-merge").PlatformAction);
    }
}
