using System.Diagnostics;
using System.Text.Json;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace CSweet.GameRoleWorkflow.Tests;

internal sealed record CheckpointStack(string Engine, string Language, string[] Libraries, string Rationale, string Setup, string? Lockfile)
{
    public const string SchemaName = "project_technical_choice";

    public static bool Matches(JsonElement request) =>
        request.TryGetProperty("responseSchemaName", out var name) && name.GetString() == SchemaName;
}

public sealed class StackCheckpointTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "AgentHarness")]
    public async Task Victor_stack_choice_reaches_Gabriels_published_setup_ticket()
    {
        var timer = Stopwatch.StartNew();
        var live = Environment.GetEnvironmentVariable("CSWEET_STACK_CHECKPOINT_MODE") switch
        {
            null or "" => false,
            "live" => true,
            var mode => throw new InvalidOperationException($"Unknown stack checkpoint mode '{mode}'.")
        };
        var model = new FirstSprintModel(false);
        if (live && Environment.GetEnvironmentVariable("CSWEET_STACK_CHECKPOINT_INPUT") is { Length: > 0 } file)
        {
            using var input = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            model = new(false) { Pitch = RequiredText(input.RootElement, "pitch"), Outcome = RequiredText(input.RootElement, "outcome") };
        }

        await using var platform = new FreshTeamSprintPlatform();
        await platform.OpenAsync(model.Pitch);
        var team = new FreshTeamSprintJourneyTests.PlanningTeam(platform, model);
        var endpoint = live ? await CheckpointModelEndpoint.OpenAsync(platform.Db, platform.Victor,
            platform.Organization, team.ProviderId) : null;
        var calls = 0;
        team.SetModel(platform.Victor, endpoint?.Model ?? "checkpoint-fixture", async (request, token) =>
        {
            if (CheckpointStack.Matches(request))
            {
                Assert.True(++calls == 1, "Victor requested another stack generation. This checkpoint measures the first answer only.");
                Assert.Contains(request.GetProperty("messages").EnumerateArray(), m => m.GetProperty("text").GetString() == model.Pitch);
                var step = Stopwatch.StartNew();
                var text = endpoint is null
                    ? JsonSerializer.Serialize(new CheckpointStack("Canvas 2D", "JavaScript", [], "Tile movement only needs grid coordinates.",
                        "Open the canvas scene and move a tile one square.", null), JsonSerializerOptions.Web)
                    : await endpoint.GenerateTextAsync(request, token);
                output.WriteLine($"{CheckpointStack.SchemaName} | {endpoint?.Model ?? "offline fixture"} | {step.Elapsed.TotalSeconds:F1}s\n{text}");
                return new { text };
            }

            // Subsequent scripted judgments check the setup that Victor actually accepted.
            if (await team.ReadStackAsync() is { } accepted) model.Stack = accepted.Payload;
            return new { text = JsonSerializer.Serialize(model.Answer(platform.Victor, request), JsonSerializerOptions.Web) };
        });

        await team.StartHostsAsync();
        await team.PlanAsync();
        Assert.Equal(1, calls);
        var acceptedStack = await team.ReadStackAsync();
        Assert.NotNull(acceptedStack);
        var stack = acceptedStack.Payload;
        var sprint = await platform.Db.WorkSprints.SingleAsync(s => s.Status == WorkSprintStatus.Active);
        var ticket = await platform.Db.CoreWorkTasks.SingleAsync(t => t.SprintId == sprint.Id && t.IsExecutable);
        Assert.Equal(team.StoryAnswer.SessionId, ticket.ProposalCoordinationSessionId);
        var planning = JsonSerializer.Deserialize<WorkItemPlanningSpecification>(ticket.PlanningSpecificationJson!, JsonSerializerOptions.Web)!;
        var decision = Assert.Single(planning.Constraints!, c => c.Contains(stack.Rationale, StringComparison.Ordinal));
        Assert.Contains(stack.Engine, decision);
        Assert.Contains(stack.Language, decision);
        Assert.All(stack.Libraries, lib => Assert.Contains(lib, decision));
        var delivery = JsonSerializer.Deserialize<WorkItemDeliverySpecification>(ticket.DeliverySpecificationJson!, JsonSerializerOptions.Web)!;
        var setup = Assert.Single(delivery.AcceptanceCriteria, c => c.Contains("clean checkout", StringComparison.OrdinalIgnoreCase) &&
            c.Contains(stack.Engine) && c.Contains(stack.Language));
        if (!string.IsNullOrWhiteSpace(stack.Lockfile)) Assert.Contains(stack.Lockfile, setup);
        var engineer = Assert.Single(platform.Roster.Members, member => member.DeclaredRoleKeys.Contains("game-engineer"));
        Assert.Equal(Guid.Parse(engineer.EmployeeId), ticket.AccountableOrganizationUserId);
        output.WriteLine($"Sprint {sprint.Sequence} | assigned setup ticket {ticket.Id} | {timer.Elapsed.TotalSeconds:F1}s from checkpoint\n{setup}");
    }

    private static string RequiredText(JsonElement input, string field) =>
        input.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString()! :
        throw new InvalidDataException($"The checkpoint requires a nonempty '{field}' string.");
}
