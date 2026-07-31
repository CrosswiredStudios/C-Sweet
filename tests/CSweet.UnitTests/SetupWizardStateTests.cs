using CSweet.UI.Setup;
using CSweet.Contracts.Llm;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;

namespace CSweet.UnitTests;

public class SetupWizardStateTests
{
    [Fact]
    public void WizardShowsFirstIncompleteStep()
    {
        var status = Status(
            defaultChatProviderId: null,
            ("welcome", true),
            ("llm-provider", false));

        var step = SetupWizardState.FirstIncompleteStepKey(
            ["welcome", "llm-provider"],
            status);

        Assert.Equal("llm-provider", step);
    }

    [Fact]
    public void LmStudioPresetPopulatesBaseUrl()
    {
        var defaults = SetupWizardState.LmStudioDefaults();

        Assert.Equal(LlmProviderType.LmStudio, defaults.ProviderType);
        Assert.Equal("Local LM Studio", defaults.ProviderName);
        Assert.Equal("http://localhost:1234/v1", defaults.BaseUrl);
        Assert.Equal("lm-studio", defaults.ApiKey);
    }

    [Fact]
    public void WizardReturnsLastVisibleStepWhenEveryVisibleStepIsComplete()
    {
        var status = Status(
            defaultChatProviderId: null,
            ("welcome", true),
            ("llm-provider", true),
            ("communications", true));

        var step = SetupWizardState.FirstIncompleteStepKey(
            ["welcome", "llm-provider", "communications"],
            status);

        Assert.Equal("communications", step);
    }

    [Fact]
    public void LocalProviderPresetsIncludeSupportedRuntimes()
    {
        var presets = SetupWizardState.LocalProviderPresets();

        Assert.Collection(
            presets,
            preset => Assert.Equal(LlmProviderType.LmStudio, preset.ProviderType),
            preset => Assert.Equal(LlmProviderType.UnslothStudio, preset.ProviderType),
            preset => Assert.Equal(LlmProviderType.Ollama, preset.ProviderType),
            preset => Assert.Equal(LlmProviderType.Vllm, preset.ProviderType));
        Assert.All(presets, preset => Assert.EndsWith("/v1", preset.BaseUrl));
    }

    [Fact]
    public void HostedProviderPresetsIncludePopularCompatibleServices()
    {
        var presets = SetupWizardState.HostedProviderPresets();

        Assert.Collection(
            presets,
            preset => Assert.Equal(LlmProviderType.OpenAi, preset.ProviderType),
            preset => Assert.Equal(LlmProviderType.GoogleGemini, preset.ProviderType),
            preset => Assert.Equal(LlmProviderType.OpenRouter, preset.ProviderType),
            preset => Assert.Equal(LlmProviderType.Groq, preset.ProviderType),
            preset => Assert.Equal(LlmProviderType.TogetherAi, preset.ProviderType));
        Assert.All(presets, preset =>
        {
            Assert.True(preset.IsHosted);
            Assert.True(preset.RequiresApiKey);
            Assert.StartsWith("https://", preset.BaseUrl);
        });
    }

    [Fact]
    public void ProviderTestErrorAppearsClearly()
    {
        var result = new ModelCapabilityTestResult(
            Guid.NewGuid(),
            ConnectionSucceeded: false,
            ChatSucceeded: false,
            StreamingSucceeded: false,
            StructuredOutputSucceeded: false,
            ToolCallingSucceeded: false,
            FailureMessage: "Provider unreachable.");

        Assert.Equal("Provider unreachable.", SetupWizardState.ProviderTestMessage(result));
    }

    private static SetupStatusResponse Status(Guid? defaultChatProviderId, params (string Key, bool Complete)[] steps)
    {
        return new SetupStatusResponse(
            IsFirstRunComplete: false,
            DefaultChatProviderId: defaultChatProviderId,
            DefaultEmbeddingProviderId: null,
            Steps: steps
                .Select(step => new OnboardingStepStatusDto(step.Key, step.Key, IsRequired: true, step.Complete))
                .ToList());
    }
}
