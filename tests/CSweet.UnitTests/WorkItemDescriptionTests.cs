using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class WorkItemDescriptionTests
{
    // A large implementation brief must survive storage and tool validation intact.
    // Character counts are deliberately independent of a model's tokenizer/context budget.
    internal static string LargeBrief => string.Concat(Enumerable.Repeat("Implementation guidance.\n", 65536));

    [Theory]
    [InlineData(WorkItemActions.Create)]
    [InlineData(WorkItemActions.RevisePlanning)]
    [InlineData(PersonalTodoActions.Add)]
    [InlineData(PersonalTodoActions.Update)]
    public void DescriptionSchemaAcceptsLargeBriefsWithoutACharacterCeiling(string capability)
    {
        var tool = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { capability }));
        var request = new JsonObject
        {
            ["boardId"] = Guid.NewGuid().ToString(), ["itemId"] = Guid.NewGuid().ToString(),
            ["title"] = "Stress scene", ["description"] = LargeBrief, ["typeKey"] = "video-game.task.v1",
            ["priority"] = "High", ["idempotencyKey"] = "large-description", ["planning"] = new JsonObject(),
            ["expectedRevision"] = 1, ["expectedPlanningRevision"] = 1
        };
        var properties = tool.InputSchema.GetProperty("properties");
        foreach (var key in request.Select(x => x.Key).ToArray())
            if (!properties.TryGetProperty(key, out _)) request.Remove(key);
        JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(request), tool.InputSchema);
        Assert.False(properties.GetProperty("description").TryGetProperty("maxLength", out _));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(request.ToJsonString()) < tool.MaximumInputBytes);
        Assert.Equal(16 * 1024 * 1024, tool.MaximumOutputBytes);
    }

    [Theory]
    [InlineData(WorkItemActions.Create)]
    [InlineData(WorkItemActions.RevisePlanning)]
    [InlineData(WorkBoardActions.Read)]
    [InlineData(PersonalTodoActions.Add)]
    [InlineData(PersonalTodoActions.Update)]
    [InlineData(PersonalTodoActions.Read)]
    public void DescriptionTransportsRespectExplicitGatewayLimits(string capability)
    {
        var catalog = new McpToolCatalog([], Options.Create(new McpGatewayOptions { MaximumRequestBytes = 8 * 1024 * 1024 }));
        var tool = Assert.Single(catalog.List(new HashSet<string> { capability }));
        Assert.Equal(8 * 1024 * 1024, tool.MaximumInputBytes);
        Assert.Equal(8 * 1024 * 1024, tool.MaximumOutputBytes);
    }

    [Fact]
    public void HumanCreateContractAcceptsTheSameLargeBrief()
    {
        var request = new CreateBoardWorkItemRequest("Stress scene", LargeBrief) { TypeKey = "video-game.task.v1" };
        Validator.ValidateObject(request, new ValidationContext(request), validateAllProperties: true);
    }

    [Fact]
    public void DescriptionMigrationUsesUnboundedTextWithoutChangingExistingContent()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        Assert.False(db.Database.HasPendingModelChanges());
        var property = db.Model.FindEntityType(typeof(WorkTask))!.FindProperty(nameof(WorkTask.Description))!;
        Assert.Null(property.GetMaxLength());
        Assert.Equal("text", property.GetColumnType());
        var migrations = db.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, x => x.EndsWith("_UnlimitWorkItemDescriptions", StringComparison.Ordinal));
        Assert.True(index > 0);
        var sql = db.GetService<IMigrator>().GenerateScript(migrations[index - 1], migrations[index]);
        Assert.Contains("ALTER TABLE \"CoreWorkTasks\" ALTER COLUMN \"Description\" TYPE text", sql);
        Assert.DoesNotContain("UPDATE \"CoreWorkTasks\"", sql);
        Assert.DoesNotContain("DROP COLUMN", sql);
    }
}
