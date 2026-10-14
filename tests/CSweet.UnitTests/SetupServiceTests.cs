using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public class SetupServiceTests
{
    [Fact]
    public async Task CreatingInitialSetupState_ReturnsIncompleteSetup()
    {
        await using var dbContext = CreateDbContext();
        var service = new SetupService(dbContext);

        var status = await service.GetStatusAsync();

        Assert.False(status.IsFirstRunComplete);
        Assert.Null(status.DefaultChatProviderId);
        Assert.Equal(5, status.Steps.Count);
        Assert.DoesNotContain(status.Steps, x => x.Key == "finish");
        Assert.Contains(status.Steps, x => x.Key == "llm-provider" && !x.IsComplete);
        Assert.Contains(status.Steps, x => x.Key == "genai-provider" && !x.IsRequired && !x.IsComplete);
        Assert.DoesNotContain(status.Steps, x => x.Key is "email-delivery" or "communications");
        Assert.DoesNotContain(status.Steps, x => x.Key == "model-capability-test");
        Assert.DoesNotContain(status.Steps, x => x.Key == "storage");
        Assert.DoesNotContain(status.Steps, x => x.Key == "worker-runtime");
    }

    [Fact]
    public async Task CompletingRequiredStep_SetsCompleteAndCompletedAt()
    {
        await using var dbContext = CreateDbContext();
        var service = new SetupService(dbContext);

        var result = await service.CompleteStepAsync("llm-provider");

        var step = await dbContext.OnboardingSteps.SingleAsync(x => x.Key == "llm-provider");
        Assert.True(result.Succeeded);
        Assert.True(step.IsComplete);
        Assert.NotNull(step.CompletedAt);
    }

    [Fact]
    public async Task FinishSetup_RequiresAssistantSelectionWithoutChangingDefaultChatProvider()
    {
        await using var dbContext = CreateDbContext();
        var service = new SetupService(dbContext);
        await service.EnsureSeededAsync();

        var provider = CreateEnabledProvider();
        dbContext.LlmProviderProfiles.Add(provider);
        var configuration = await dbContext.SystemConfigurations.SingleAsync();
        configuration.PersonalAssistantProviderId = provider.Id;
        configuration.PersonalAssistantModel = "assistant-model";
        await CompletePriorRequiredStepsAsync(dbContext);

        var result = await service.CompleteFirstRunAsync();

        Assert.True(result.Succeeded);
        Assert.Null((await dbContext.SystemConfigurations.SingleAsync()).DefaultChatProviderId);
    }

    [Fact]
    public async Task FinishSetup_FailsWhenNoEnabledProviderExists()
    {
        await using var dbContext = CreateDbContext();
        var service = new SetupService(dbContext);
        await service.EnsureSeededAsync();

        await CompletePriorRequiredStepsAsync(dbContext);

        var result = await service.CompleteFirstRunAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("provider_profile_required", result.ErrorCode);
    }

    [Fact]
    public async Task FinishSetup_SucceedsWhenPrerequisitesExistAndWritesAuditEvent()
    {
        await using var dbContext = CreateDbContext();
        var service = new SetupService(dbContext);
        await service.EnsureSeededAsync();

        var provider = CreateEnabledProvider();
        dbContext.LlmProviderProfiles.Add(provider);
        var configuration = await dbContext.SystemConfigurations.SingleAsync();
        configuration.PersonalAssistantProviderId = provider.Id;
        configuration.PersonalAssistantModel = "assistant-model";
        await CompletePriorRequiredStepsAsync(dbContext);

        var result = await service.CompleteFirstRunAsync();

        Assert.True(result.Succeeded);
        Assert.True((await dbContext.SystemConfigurations.SingleAsync()).IsFirstRunComplete);
        Assert.DoesNotContain(result.Status!.Steps, x => x.Key is "email-delivery" or "communications");
        Assert.Contains(await dbContext.AuditEvents.ToListAsync(), x => x.EventType == "setup.first_run.completed");
    }

    [Fact]
    public async Task RemovedIntegrationStepsDoNotBlockExistingInstallations()
    {
        await using var dbContext = CreateDbContext();
        var service = new SetupService(dbContext);
        await service.EnsureSeededAsync();
        foreach (var key in new[] { "email-delivery", "communications" })
            dbContext.OnboardingSteps.Add(new OnboardingStep { Id = Guid.NewGuid(), Key = key, DisplayName = key, IsRequired = true });
        dbContext.LlmProviderProfiles.Add(CreateEnabledProvider());
        (await dbContext.SystemConfigurations.SingleAsync()).IsFirstRunComplete = true;
        await dbContext.SaveChangesAsync();

        var status = await service.GetStatusAsync();
        Assert.Equal(5, status.Steps.Count);
        Assert.All(await dbContext.OnboardingSteps.Where(x => x.Key == "email-delivery" || x.Key == "communications").ToListAsync(),
            step => { Assert.False(step.IsRequired); Assert.False(step.IsComplete); });
        await CompletePriorRequiredStepsAsync(dbContext);

        var result = await service.CompleteFirstRunAsync();
        Assert.True(result.Succeeded);
        Assert.True(result.Status!.IsFirstRunComplete);
    }

    [Fact]
    public async Task NewSetupCannotFinishWithoutAssistantProviderAndModel()
    {
        await using var db = CreateDbContext(); var service = new SetupService(db);
        await service.EnsureSeededAsync(); db.LlmProviderProfiles.Add(CreateEnabledProvider());
        await CompletePriorRequiredStepsAsync(db);
        var result = await service.CompleteFirstRunAsync();
        Assert.False(result.Succeeded); Assert.Equal("assistant_model_required", result.ErrorCode);
    }

    [Fact]
    public async Task AssistantStepRequiresSavedSettingsAndEnabledProvider()
    {
        await using var db = CreateDbContext();
        var service = new SetupService(db);
        Assert.False((await service.CompleteStepAsync("personal-assistant")).Succeeded);
        var provider = CreateEnabledProvider();
        db.LlmProviderProfiles.Add(provider);
        var configuration = await db.SystemConfigurations.SingleAsync();
        configuration.PersonalAssistantProviderId = provider.Id;
        configuration.PersonalAssistantModel = "model";
        provider.IsEnabled = false;
        await db.SaveChangesAsync();
        Assert.False((await service.CompleteStepAsync("personal-assistant")).Succeeded);
        provider.IsEnabled = true;
        await db.SaveChangesAsync();
        Assert.True((await service.CompleteStepAsync("personal-assistant")).Succeeded);
    }

    [Fact]
    public async Task AddingAssistantStepDoesNotReopenCompletedSetup()
    {
        await using var db = CreateDbContext();
        db.SystemConfigurations.Add(new SystemConfiguration { Id = Guid.NewGuid(), IsFirstRunComplete = true });
        await db.SaveChangesAsync();
        var service = new SetupService(db);
        var status = await service.GetStatusAsync();
        Assert.True(status.IsFirstRunComplete);
        Assert.Contains(status.Steps, x => x.Key == "personal-assistant" && x.IsComplete);
        Assert.True((await service.CompleteFirstRunAsync()).Succeeded);
    }

    private static CSweetDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new CSweetDbContext(options);
    }

    private static LlmProviderProfile CreateEnabledProvider()
    {
        return new LlmProviderProfile
        {
            Id = Guid.NewGuid(),
            Name = "Local LM Studio",
            ProviderType = LlmProviderType.LmStudio,
            BaseUrl = "http://localhost:1234/v1",
            DefaultChatModel = string.Empty,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static async Task CompletePriorRequiredStepsAsync(CSweetDbContext dbContext)
    {
        var now = DateTimeOffset.UtcNow;
        var steps = await dbContext.OnboardingSteps
            .Where(x => x.IsRequired)
            .ToListAsync();

        foreach (var step in steps)
        {
            step.IsComplete = true;
            step.CompletedAt = now;
            step.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync();
    }
}
