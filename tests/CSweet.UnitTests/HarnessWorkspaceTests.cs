using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace CSweet.UnitTests;

public sealed class HarnessWorkspaceTests
{
    [Theory]
    [InlineData("{\"choice\":\"Relevant\"}", true)]
    [InlineData("{\"choice\":\"Approve\"}", false)]
    [InlineData("{\"choice\":\"Relevant\",\"grant\":true}", false)]
    [InlineData("{\"choice\":\"Relevant\",\"choice\":\"Relevant\"}", false)]
    [InlineData("[\"Relevant\"]", false)]
    [InlineData("Relevant", false)]
    public void JudgmentsOnlyAcceptTheCodeBuiltChoiceSet(string output, bool valid)
    {
        var result = HarnessJudgments.Validate(HarnessJudgments.Options("Relevance"), output);
        Assert.Equal(valid, result.Valid); Assert.True(result.Advisory);
        if (!valid) Assert.Null(result.Choice);
    }

    [Fact]
    public void MonetaryCeilingsRequirePricesAndRoutesAreUnique()
    {
        Assert.Throws<ArgumentException>(() => HarnessWorkspaceService.Validate(new(TaskCostLimit: 1)));
        var route = new HarnessRoute("Default", Guid.NewGuid(), "model");
        Assert.Throws<ArgumentException>(() => HarnessWorkspaceService.Validate(new(TaskCostLimit: 1, Routes: [route])));
        Assert.Throws<ArgumentException>(() => HarnessWorkspaceService.Validate(new(Routes: [route, route])));
        HarnessWorkspaceService.Validate(new(TaskCostLimit: 1, Routes: [route with { InputPricePerMillion = 0, OutputPricePerMillion = 1 }]));
    }

    [Fact]
    public void CompactionPreservesProtocolRecentResultAndChangedSource()
    {
        var original = new string('x', 5000);
        ChatMessage Call(string id) => new(ChatRole.Assistant, [new FunctionCallContent(id, "read_file", new Dictionary<string, object?> { ["path"] = "a.txt" })]);
        ChatMessage Result(string id, string text) => new(ChatRole.Tool, [new FunctionResultContent(id, text)]);
        var messages = new[] { new ChatMessage(ChatRole.System, "Keep these instructions"), Call("a"), Result("a", original), Call("b"), Result("b", original), Call("c"), Result("c", original + "changed") };
        var changes = HarnessContextOptimizer.Compact(messages);
        Assert.Equal("a", Assert.Single(changes).CallId); Assert.Equal("b", changes[0].RetainedCallId);
        Assert.Equal(original, ((FunctionResultContent)messages[4].Contents[0]).Result);
        Assert.Equal(original + "changed", ((FunctionResultContent)messages[6].Contents[0]).Result);
        Assert.Equal("Keep these instructions", messages[0].Text);
        Assert.Equal(3, messages.SelectMany(x => x.Contents).OfType<FunctionCallContent>().Count());
        Assert.Equal(3, messages.SelectMany(x => x.Contents).OfType<FunctionResultContent>().Count());
    }

    [Fact]
    public async Task SharedTaskBudgetSerializesIndependentHostsAndSettlesKnownUsageOnly()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var root = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        AgentRunLog Run(Guid installation) => new() { Id = Guid.NewGuid(), AgentInstallationId = installation, WorkItemId = Guid.NewGuid(), AncestorWorkItemIdsJson = JsonSerializer.Serialize(new[] { root }), Status = "Running" };
        var a = Run(first); var b = Run(second);
        var preparation = new HarnessPreparation(new(TaskTokenLimit: 100), "Default", null, "", []);
        await using var dbA = new CSweetDbContext(options); await using var dbB = new CSweetDbContext(options);
        var sessionA = new HarnessBudgetSession(dbA, a, preparation, "business", first, 10, 50);
        var sessionB = new HarnessBudgetSession(dbB, b, preparation, "business", second, 10, 50);
        await sessionA.ReserveAsync(default);
        await Assert.ThrowsAsync<HarnessBudgetExceededException>(() => sessionB.ReserveAsync(default));
        a.Status = "Completed"; a.TokenInputCount = 10; a.TokenOutputCount = 5;
        await sessionA.DisposeAsync();
        await sessionB.ReserveAsync(default);
        await sessionB.DisposeAsync(); // Unknown usage stays reserved.
        await using var verify = new CSweetDbContext(options);
        Assert.Equal(75, (await verify.HarnessTaskBudgets.SingleAsync()).ChargedTokens);
        Assert.Equal(2, await verify.HarnessBudgetReservations.CountAsync());
    }

    [Fact]
    public async Task RevokedSkillOrPolicyCannotDispatchPreparedInstructions()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var installation = Guid.NewGuid(); var skill = new BusinessSkillRevision { Id = Guid.NewGuid(), BusinessId = "business", Name = "Review", Version = 1, Instructions = "Review evidence", Digest = "digest" };
        db.AgentInstallations.Add(new() { Id = installation, BusinessId = "business" });
        db.AgentInstallationGrants.Add(new() { Id = Guid.NewGuid(), AgentInstallationId = installation });
        db.BusinessSkillRevisions.Add(skill);
        var policy = new HarnessPolicy(1, SkillRevisionIds: [skill.Id]);
        var row = new AgentHarnessPolicy { Id = installation, Revision = 1, PolicyJson = JsonSerializer.Serialize(policy, HarnessWorkspaceService.Json) };
        db.AgentHarnessPolicies.Add(row); await db.SaveChangesAsync();
        var prepared = await HarnessRuntime.PrepareAsync(db, installation, null, default);
        Assert.Contains("Review evidence", prepared.SkillInstructions);
        skill.Enabled = false; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<HarnessPolicyChangedException>(() => HarnessRuntime.ValidateAsync(db, installation, prepared, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => HarnessRuntime.PrepareAsync(db, installation, null, default));
        skill.Enabled = true; row.Revision++; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<HarnessPolicyChangedException>(() => HarnessRuntime.ValidateAsync(db, installation, prepared, default));
    }
    [Fact]
    public async Task DelegatedCallsInheritRootCeilingEvenBeforeRootHasReserved()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new CSweetDbContext(options);
        var organization = Guid.NewGuid(); var owner = Guid.NewGuid(); var child = Guid.NewGuid(); var root = Guid.NewGuid();
        db.AgentInstallations.Add(new() { Id = owner, BusinessId = organization.ToString("D") });
        db.AgentHarnessPolicies.Add(new() { Id = owner, Revision = 1, PolicyJson = JsonSerializer.Serialize(new HarnessPolicy(1, TaskTokenLimit: 50), HarnessWorkspaceService.Json) });
        db.CoreWorkTasks.Add(new() { Id = root, OrganizationId = organization, AssignedAgentInstallationId = owner, Title = "Root task" });
        await db.SaveChangesAsync();
        var run = new AgentRunLog { Id = Guid.NewGuid(), WorkItemId = Guid.NewGuid(), AncestorWorkItemIdsJson = JsonSerializer.Serialize(new[] { root }), Status = "Running" };
        await using var session = new HarnessBudgetSession(db, run, new(new(), "Default", null, "", []), organization.ToString("D"), child, 30, 30);
        await Assert.ThrowsAsync<HarnessBudgetExceededException>(() => session.ReserveAsync(default));
        Assert.Empty(await db.HarnessBudgetReservations.ToListAsync());
    }

    [Fact]
    public async Task PriorUnpricedCallsCannotBeReclassifiedAsZeroCostWhenAMonetaryCeilingIsAdded()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new CSweetDbContext(options); var installation = Guid.NewGuid(); var root = Guid.NewGuid();
        var run = new AgentRunLog { Id = Guid.NewGuid(), WorkItemId = root, Status = "Failed" };
        await using (var unpriced = new HarnessBudgetSession(db, run, new(new(TaskTokenLimit: 1000), "Default", null, "", []), "business", installation, 10, 10))
            await unpriced.ReserveAsync(default);
        var pricedPolicy = new HarnessPreparation(new(TaskCostLimit: 1), "Default", new("Default", Guid.NewGuid(), "model", 1, 1), "", []);
        await using var priced = new HarnessBudgetSession(db, run, pricedPolicy, "business", installation, 10, 10);
        await Assert.ThrowsAsync<HarnessBudgetExceededException>(() => priced.ReserveAsync(default));
        Assert.Single(await db.HarnessBudgetReservations.ToListAsync());
    }
}
