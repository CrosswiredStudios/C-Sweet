using CSweet.Contracts.Llm;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using CSweet.Domain.Core;

namespace CSweet.UnitTests;

public partial class LlmProviderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DefaultProviderCannotBeDeletedOrDisabledUntilAnotherDefaultIsSelected(bool otherEnabled)
    {
        await using var db = CreateDbContext(); var first = await AddProfileAsync(db); var other = await AddProfileAsync(db);
        other.IsEnabled = otherEnabled;
        db.SystemConfigurations.Add(new() { Id = Guid.NewGuid(), DefaultChatProviderId = first.Id });
        await db.SaveChangesAsync();
        var secrets = new InMemoryLlmProviderSecretStore();
        first.ApiKeySecretName = "first-provider-key"; await secrets.StoreAsync(first.ApiKeySecretName, "keep-this-key"); await db.SaveChangesAsync();
        var service = new LlmProviderProfileService(db, secrets, CreateTester(db, CreateReadyHandler()));
        var deleted = await service.DeleteAsync(first.Id);
        var disabled = await service.UpdateAsync(first.Id, new(first.Name, first.ProviderType, first.BaseUrl, null, false,
            first.DefaultChatModel, null, null, null, true, false, false, false, false));
        Assert.Equal("default_provider_replacement_required", deleted.ErrorCode);
        Assert.Equal("default_provider_replacement_required", disabled.ErrorCode);
        Assert.Equal(2, await db.LlmProviderProfiles.CountAsync()); Assert.True(first.IsEnabled);
        Assert.Equal(first.Id, (await db.SystemConfigurations.SingleAsync()).DefaultChatProviderId);
        Assert.Equal("keep-this-key", await secrets.GetAsync(first.ApiKeySecretName));
        // Choosing an enabled, tested replacement is explicit. Deletion never guesses one.
        other.IsEnabled = true;
        db.ModelCapabilityTests.Add(new() { Id = Guid.NewGuid(), ProviderProfileId = other.Id, ChatSucceeded = true });
        await db.SaveChangesAsync();
        Assert.True((await service.SetDefaultChatProviderAsync(other.Id)).Succeeded);
        Assert.True((await service.DeleteAsync(first.Id)).Succeeded);
        Assert.Equal(other.Id, (await db.SystemConfigurations.SingleAsync()).DefaultChatProviderId);
        Assert.Null(await secrets.GetAsync("first-provider-key"));
    }

    [Fact]
    public async Task DefaultSelectionRequiresAnEnabledProviderAndAnExplicitModel()
    {
        await using var db = CreateDbContext(); var profile = await AddProfileAsync(db);
        db.ModelCapabilityTests.Add(new() { Id = Guid.NewGuid(), ProviderProfileId = profile.Id, ChatSucceeded = true });
        profile.DefaultChatModel = ""; await db.SaveChangesAsync();
        var service = new LlmProviderProfileService(db, new InMemoryLlmProviderSecretStore(), CreateTester(db, CreateReadyHandler()));
        Assert.Equal("default_model_required", (await service.SetDefaultChatProviderAsync(profile.Id)).ErrorCode);
        profile.DefaultChatModel = "chosen-model"; profile.IsEnabled = false; await db.SaveChangesAsync();
        Assert.Equal("provider_profile_disabled", (await service.SetDefaultChatProviderAsync(profile.Id)).ErrorCode);
        profile.IsEnabled = true; await db.SaveChangesAsync();
        Assert.True((await service.SetDefaultChatProviderAsync(profile.Id)).Succeeded);
        db.ChangeTracker.Clear();
        Assert.Equal(profile.Id, (await db.SystemConfigurations.SingleAsync()).DefaultChatProviderId);
        Assert.Equal("chosen-model", (await db.LlmProviderProfiles.SingleAsync()).DefaultChatModel);
    }

    [Fact]
    public async Task SavingDefaultPairDurablyWakesCurrentChiefsWithUnresolvedHiring()
    {
        await using var db = CreateDbContext(); var profile = await AddProfileAsync(db);
        var organization = Guid.NewGuid(); var chief = Guid.NewGuid(); var installation = Guid.NewGuid();
        db.Add(new Organization { Id = organization, Name = "Hiring business" });
        db.Add(new AgentInstallation { Id = installation, BusinessId = organization.ToString(), IsEnabled = true });
        db.Add(new OrganizationUser { Id = chief, OrganizationId = organization, AgentInstallationId = installation, IsActive = true });
        db.Add(new LeadershipAssignment { Id = Guid.NewGuid(), OrganizationId = organization, OrganizationUserId = chief, PositionKey = LeadershipPositionKeys.ChiefOfStaff });
        db.Add(new WorkforcePlan { Id = Guid.NewGuid(), OrganizationId = organization, RequestingInstallationId = installation,
            Status = ProposalStatus.Pending, Headcount = 1 });
        db.Add(new AgentSchedule { Id = Guid.NewGuid(), AgentInstallationId = installation, IsEnabled = true, NextAttentionReviewAt = DateTimeOffset.UtcNow.AddDays(1) });
        db.ModelCapabilityTests.Add(new() { Id = Guid.NewGuid(), ProviderProfileId = profile.Id, ChatSucceeded = true });
        await db.SaveChangesAsync();
        var before = DateTimeOffset.UtcNow;
        var service = new LlmProviderProfileService(db, new InMemoryLlmProviderSecretStore(), CreateTester(db, CreateReadyHandler()));
        Assert.True((await service.SetDefaultChatProviderAsync(profile.Id)).Succeeded);
        db.ChangeTracker.Clear(); var schedule = await db.AgentSchedules.SingleAsync();
        Assert.Equal("llm.default-selection-changed", schedule.PendingAttentionTriggerCategory);
        Assert.Equal(profile.Id, schedule.PendingAttentionCorrelationId);
        Assert.InRange(schedule.NextAttentionReviewAt!.Value, before, DateTimeOffset.UtcNow);
    }

    [LlmDefaultPostgresFact]
    public async Task ConcurrentDefaultSelectionAndDeletionCannotLeaveADanglingDefault()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_HIRING_TEST_POSTGRES"));
        Assert.True(connection.Host is "localhost" or "127.0.0.1");
        connection.Database = "csweet_llm_default_test_" + Guid.NewGuid().ToString("N"); connection.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var setup = new CSweetDbContext(options);
        try
        {
            await setup.Database.EnsureCreatedAsync(); var first = await AddProfileAsync(setup); var second = await AddProfileAsync(setup);
            setup.SystemConfigurations.Add(new() { Id = Guid.NewGuid(), DefaultChatProviderId = first.Id });
            setup.ModelCapabilityTests.Add(new() { Id = Guid.NewGuid(), ProviderProfileId = second.Id, ChatSucceeded = true });
            await setup.SaveChangesAsync();
            async Task<LlmProviderProfileActionResponse> Act(bool deleting)
            {
                await using var db = new CSweetDbContext(options);
                var service = new LlmProviderProfileService(db, new InMemoryLlmProviderSecretStore(), CreateTester(db, CreateReadyHandler()));
                return deleting ? await service.DeleteAsync(second.Id) : await service.SetDefaultChatProviderAsync(second.Id);
            }
            var results = await Task.WhenAll(Act(true), Act(false));
            Assert.NotEqual(results[0].Succeeded, results[1].Succeeded);
            var selected = (await setup.SystemConfigurations.AsNoTracking().SingleAsync()).DefaultChatProviderId;
            Assert.NotNull(selected);
            Assert.True(await setup.LlmProviderProfiles.AnyAsync(x => x.Id == selected && x.IsEnabled));
            if (!results[0].Succeeded) Assert.Equal("default_provider_replacement_required", results[0].ErrorCode);
        }
        finally { await setup.Database.EnsureDeletedAsync(); }
    }

    private sealed class LlmDefaultPostgresFactAttribute : FactAttribute
    {
        public LlmDefaultPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_HIRING_TEST_POSTGRES")))
                Skip = "Set CSWEET_HIRING_TEST_POSTGRES to a disposable loopback PostgreSQL server.";
        }
    }
}
