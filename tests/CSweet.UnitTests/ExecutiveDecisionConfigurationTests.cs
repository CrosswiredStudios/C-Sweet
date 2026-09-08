using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class ExecutiveDecisionServiceTests
{
    [Theory]
    [InlineData("apply", "game-studio", 1)]
    [InlineData("leave-unchanged", "general", 0)]
    public async Task ConfigurationChoice_PreservesOtherSettingsAndResumesExactlyOnce(string choice, string expected, int writes)
    {
        await using var db = CreateDb();
        var setup = await SeedAsync(db);
        var settings = new ChoiceConfigurations();
        var service = await ConfigurationServiceAsync(db, setup, settings);
        var card = await service.CreateAsync(ConfigurationCommand(setup));
        Assert.False(card.AllowFreeText);
        Assert.Equal(["apply", "leave-unchanged"], card.Options.Select(x => x.Id));
        Assert.Contains("Current Business Operating Profile: General", card.Prompt);
        Assert.Equal("Switch to Game Studio", card.Options[0].Label);
        Assert.Equal("Leave unchanged", card.Options[1].Label);
        var request = new AnswerExecutiveDecisionRequest(choice, null, "answer-profile");
        var result = await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, card.Id, setup.OwnerId, request);
        var replay = await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, card.Id, setup.OwnerId, request);
        Assert.True(result.Succeeded, result.Message);
        Assert.True(replay.Succeeded);
        Assert.Null(result.Turn);
        Assert.Equal(writes, settings.Writes);
        Assert.Equal(expected, settings.View.EffectiveValues["businessOperatingProfile"].GetString());
        Assert.Equal("keep-model", settings.View.Overrides["llmModel"].GetString());
        Assert.Single(db.AgentWorkItems, x => x.Name == ExecutiveDecisionService.ConfigurationChoiceAnsweredEvent);
        Assert.Single(db.CoreConversationMessages, x => x.CorrelationId == card.Id);
        Assert.Single(db.ChatTurns);
        Assert.Equal("Answered", result.Decision!.Status);
        Assert.False(result.Decision.AllowFreeText);
    }

    [Fact]
    public async Task ConfigurationChoice_RejectsFreeTextAndNonOwnerWithoutMutation()
    {
        await using var db = CreateDb();
        var setup = await SeedAsync(db);
        var settings = new ChoiceConfigurations();
        var service = await ConfigurationServiceAsync(db, setup, settings);
        var card = await service.CreateAsync(ConfigurationCommand(setup));
        var freeText = await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, card.Id, setup.OwnerId,
            new(null, "custom", "answer-free"));
        Assert.False(freeText.Succeeded);
        var owner = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == setup.OwnerId);
        owner.PermissionLevel = OrganizationPermissionLevel.Manager;
        await db.SaveChangesAsync();
        var denied = await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, card.Id, setup.OwnerId,
            new("apply", null, "answer-manager"));
        Assert.Equal("not_authorized", denied.ErrorCode);
        Assert.Equal(0, settings.Writes);
        Assert.Empty(db.AgentWorkItems);
        Assert.Equal(ExecutiveDecisionStatus.Pending, (await db.ExecutiveDecisions.SingleAsync()).Status);
    }

    [Fact]
    public async Task ConfigurationChoice_StaleSwitchIsRejectedButLeaveUnchangedPreservesLatestSettings()
    {
        await using var db = CreateDb();
        var setup = await SeedAsync(db);
        var settings = new ChoiceConfigurations();
        var service = await ConfigurationServiceAsync(db, setup, settings);
        var card = await service.CreateAsync(ConfigurationCommand(setup));
        settings.SetProfile("saas");
        var result = await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, card.Id, setup.OwnerId,
            new("apply", null, "answer-stale"));
        Assert.Equal("configuration_conflict", result.ErrorCode);
        Assert.Equal(0, settings.Writes);
        var unchanged = await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, card.Id, setup.OwnerId,
            new("leave-unchanged", null, "answer-keep"));
        Assert.True(unchanged.Succeeded);
        Assert.Equal("saas", settings.View.EffectiveValues["businessOperatingProfile"].GetString());
        Assert.Equal(0, settings.Writes);
    }

    [Fact]
    public async Task ConfigurationChoice_RejectsUnknownPresetAndNonSelectField()
    {
        await using var db = CreateDb();
        var setup = await SeedAsync(db);
        var service = await ConfigurationServiceAsync(db, setup, new ChoiceConfigurations());
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(ConfigurationCommand(setup) with
            { ConfigurationChange = new("businessOperatingProfile", "general", "invented") }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(ConfigurationCommand(setup) with
            { ConfigurationChange = new("llmModel", "keep-model", "other-model") }));
        Assert.Empty(db.ExecutiveDecisions);
    }

    [Fact]
    public async Task LegacyOptionsArray_RemainsReadableWithFreeTextEnabled()
    {
        await using var db = CreateDb();
        var setup = await SeedAsync(db);
        var service = new ExecutiveDecisionService(db, new ChatTurnService(db));
        var card = await service.CreateAsync(new(setup.OrganizationId, setup.ConversationId, setup.TurnId, null,
            setup.InstallationId, "Choose", [new("a", "Proceed", null), new("b", "Wait", null)], "a", "legacy"));
        (await db.ExecutiveDecisions.SingleAsync()).OptionsJson = """[{"id":"a","label":"Proceed"},{"id":"b","label":"Wait"}]""";
        await db.SaveChangesAsync();
        var cards = await service.ListForMessagesAsync(setup.OrganizationId, setup.ConversationId);
        Assert.True(cards[setup.TurnId].AllowFreeText);
        Assert.Equal(2, cards[setup.TurnId].Options.Count);
    }

    [Fact]
    public async Task AcceptedChoice_IsPersistedAsEmployeeOverrideAndReloadsFromTheConfigurationService()
    {
        await using var db = CreateDb();
        var setup = await SeedAsync(db);
        await ConfigurationServiceAsync(db, setup, new ChoiceConfigurations());
        var installation = await db.AgentInstallations.SingleAsync();
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(), PackageSourceId = Guid.NewGuid(), AgentId = "com.csweet.chief-of-staff",
            ManifestJson = """
            {"configuration":[
              {"key":"businessOperatingProfile","label":"Business Operating Profile","type":"select","required":true,"options":[{"value":"general","label":"General"},{"value":"game-studio","label":"Game Studio"}]},
              {"key":"llmModel","label":"Model","type":"text","required":false}
            ]}
            """
        };
        var definition = new AgentDefinition
        {
            Id = Guid.NewGuid(), AgentId = package.AgentId, PackageSourceId = package.PackageSourceId,
            PackageVersionId = package.Id, PackageVersion = package,
            Configuration = new AgentDefinitionConfiguration
            {
                Id = Guid.NewGuid(), SchemaVersion = "2.0", Revision = 1,
                SettingsJson = """{"businessOperatingProfile":"general","llmModel":"default-model"}"""
            }
        };
        installation.PackageVersion = package;
        installation.PackageVersionId = package.Id;
        installation.AgentDefinition = definition;
        installation.AgentDefinitionId = definition.Id;
        installation.Configuration = new AgentInstallationConfiguration
        {
            Id = Guid.NewGuid(), AgentInstallationId = installation.Id, SchemaVersion = "2.0", Revision = 1,
            SettingsJson = """{"llmModel":"keep-model"}"""
        };
        db.AgentInstallationConfigurations.Add(installation.Configuration);
        db.AgentDefinitions.Add(definition);
        db.AgentPackageVersions.Add(package);
        await db.SaveChangesAsync();
        var configurations = new AgentInstallationConfigurationService(db, new TestAuditEventWriter());
        var router = new AgentWorkRouter(db, new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System), TimeProvider.System);
        var service = new ExecutiveDecisionService(db, new ChatTurnService(db), configurations: configurations, router: router);
        var card = await service.CreateAsync(ConfigurationCommand(setup));
        var result = await service.AnswerAsync(setup.OrganizationId, setup.ConversationId, card.Id, setup.OwnerId,
            new("apply", null, "persist-profile"));
        Assert.True(result.Succeeded, result.Message);
        db.ChangeTracker.Clear();
        var employee = await db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == setup.InstallationId);
        var reloaded = await new AgentInstallationConfigurationService(db, new TestAuditEventWriter())
            .GetEmployeeAsync(setup.OrganizationId, employee.Id);
        Assert.Equal("game-studio", reloaded.EffectiveValues["businessOperatingProfile"].GetString());
        Assert.Equal("game-studio", reloaded.Overrides["businessOperatingProfile"].GetString());
        Assert.Equal("general", reloaded.DefaultValues["businessOperatingProfile"].GetString());
        Assert.Equal("keep-model", reloaded.EffectiveValues["llmModel"].GetString());
        Assert.Equal(2, reloaded.ExpectedRevision);
        Assert.Single(db.AgentWorkItems);
    }

    private static CreateExecutiveDecisionCommand ConfigurationCommand(Setup setup) => new(
        setup.OrganizationId, setup.ConversationId, null, setup.AssistantMessageId, setup.InstallationId,
        "Model-written suggestion", [new("apply", "Switch", null), new("leave-unchanged", "Leave unchanged", null)], "apply", "profile-choice")
        { ConfigurationChange = new("businessOperatingProfile", "general", "game-studio") };

    private static async Task<ExecutiveDecisionService> ConfigurationServiceAsync(CSweetDbContext db, Setup setup, ChoiceConfigurations settings)
    {
        db.AgentInstallations.Add(new AgentInstallation { Id = setup.InstallationId, BusinessId = setup.OrganizationId.ToString("D"),
            Grant = new AgentInstallationGrant { Id = Guid.NewGuid(), AgentInstallationId = setup.InstallationId,
                EventSubscriptionsJson = JsonSerializer.Serialize(new[] { ExecutiveDecisionService.ConfigurationChoiceAnsweredEvent }) } });
        await db.SaveChangesAsync();
        var router = new AgentWorkRouter(db, new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System), TimeProvider.System);
        return new ExecutiveDecisionService(db, new ChatTurnService(db), configurations: settings, router: router);
    }

    private sealed class ChoiceConfigurations : IAgentConfigurationService
    {
        public int Writes { get; private set; }
        public AgentConfigurationView View { get; private set; } = new("chief", "2.4.0", "2.0",
            [new("businessOperatingProfile", "Business Operating Profile", "select", true,
                Options: [new("general", "General"), new("game-studio", "Game Studio"), new("saas", "SaaS")]),
             new("llmModel", "Model", "llm-model", true)],
            new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement> { ["llmModel"] = JsonSerializer.SerializeToElement("keep-model") },
            new Dictionary<string, JsonElement> { ["businessOperatingProfile"] = JsonSerializer.SerializeToElement("general") }, [], 4, 4, 4, "Current");
        public void SetProfile(string value) => View = View with { EffectiveValues = new Dictionary<string, JsonElement>
            { ["businessOperatingProfile"] = JsonSerializer.SerializeToElement(value) } };
        public Task<AgentConfigurationView> GetEmployeeAsync(Guid org, Guid employee, CancellationToken token = default) => Task.FromResult(View);
        public Task<AgentConfigurationView> SaveEmployeeOverridesAsync(Guid org, Guid employee, PutAgentConfigurationOverridesRequest request, CancellationToken token = default)
        {
            Assert.Equal(View.ExpectedRevision, request.ExpectedRevision);
            Writes++;
            View = View with { Overrides = request.Overrides, EffectiveValues = request.Overrides, ExpectedRevision = View.ExpectedRevision + 1 };
            return Task.FromResult(View);
        }
        public Task<AgentConfigurationView> GetDefinitionAsync(Guid id, CancellationToken token = default) => throw new NotSupportedException();
        public Task<AgentConfigurationView> SaveDefinitionAsync(Guid id, PutAgentDefinitionConfigurationRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<AgentConfigurationView> RestoreEmployeeOverrideAsync(Guid org, Guid employee, string key, long revision, CancellationToken token = default) => throw new NotSupportedException();
        public Task<AgentConfigurationView> RestoreAllEmployeeOverridesAsync(Guid org, Guid employee, long revision, CancellationToken token = default) => throw new NotSupportedException();
        public Task<EffectiveAgentConfiguration> ResolveInstallationAsync(Guid id, CancellationToken token = default) => throw new NotSupportedException();
    }
}
