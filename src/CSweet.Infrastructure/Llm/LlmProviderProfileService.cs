using CSweet.AI.Providers;
using CSweet.Contracts.Llm;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Llm;

public sealed class LlmProviderProfileService : ILlmProviderProfileService
{
    private readonly CSweetDbContext _dbContext;
    private readonly ILlmProviderSecretStore _secretStore;
    private readonly ILlmConnectionTester _connectionTester;

    public LlmProviderProfileService(
        CSweetDbContext dbContext,
        ILlmProviderSecretStore secretStore,
        ILlmConnectionTester connectionTester)
    {
        _dbContext = dbContext;
        _secretStore = secretStore;
        _connectionTester = connectionTester;
    }

    public async Task<LlmProviderProfileActionResponse> CreateAsync(
        CreateLlmProviderProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationFailure = Validate(request.Name, request.BaseUrl, request.MaximumConcurrentRequests);
        if (validationFailure is not null)
        {
            return validationFailure;
        }

        var now = DateTimeOffset.UtcNow;
        var profileId = Guid.NewGuid();
        string? apiKeySecretName = null;

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            apiKeySecretName = $"llm-provider-profiles/{profileId}/api-key";
            await _secretStore.StoreAsync(apiKeySecretName, request.ApiKey, cancellationToken);
        }

        var profile = new LlmProviderProfile
        {
            Id = profileId,
            Name = request.Name.Trim(),
            ProviderType = request.ProviderType,
            BaseUrl = request.BaseUrl.Trim(),
            ApiKeySecretName = apiKeySecretName,
            DefaultChatModel = request.DefaultChatModel.Trim(),
            DefaultEmbeddingModel = string.IsNullOrWhiteSpace(request.DefaultEmbeddingModel)
                ? null
                : request.DefaultEmbeddingModel.Trim(),
            ContextWindowTokens = request.ContextWindowTokens,
            MaxOutputTokens = request.MaxOutputTokens,
            MaximumConcurrentRequests = request.MaximumConcurrentRequests,
            SupportsStreaming = request.SupportsStreaming,
            SupportsToolCalling = request.SupportsToolCalling,
            SupportsStructuredOutput = request.SupportsStructuredOutput,
            SupportsVision = request.SupportsVision,
            IsEnabled = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.LlmProviderProfiles.Add(profile);
        _dbContext.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "llm_provider_profile.created",
            EntityType = nameof(LlmProviderProfile),
            EntityId = profile.Id,
            Summary = $"LLM provider profile created: {profile.Name}",
            CreatedAt = now
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new LlmProviderProfileActionResponse(true, null, null, profile.ToResponse());
    }

    public async Task<LlmProviderProfileActionResponse> UpdateAsync(
        Guid providerProfileId,
        UpdateLlmProviderProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationFailure = Validate(request.Name, request.BaseUrl, request.MaximumConcurrentRequests);
        if (validationFailure is not null)
        {
            return validationFailure;
        }

        var profile = await _dbContext.LlmProviderProfiles
            .SingleOrDefaultAsync(x => x.Id == providerProfileId, cancellationToken);

        if (profile is null)
        {
            return Failure("provider_profile_not_found", "Provider profile was not found.");
        }

        var connectionChanged =
            profile.ProviderType != request.ProviderType ||
            !string.Equals(profile.BaseUrl, request.BaseUrl.Trim(), StringComparison.Ordinal) ||
            !string.Equals(profile.DefaultChatModel, request.DefaultChatModel.Trim(), StringComparison.Ordinal) ||
            !string.Equals(profile.DefaultEmbeddingModel, NormalizeOptional(request.DefaultEmbeddingModel), StringComparison.Ordinal) ||
            request.ReplaceApiKey;

        if (request.ReplaceApiKey)
        {
            if (string.IsNullOrWhiteSpace(request.ApiKey))
            {
                profile.ApiKeySecretName = null;
            }
            else
            {
                profile.ApiKeySecretName ??= $"llm-provider-profiles/{profile.Id}/api-key";
                await _secretStore.StoreAsync(profile.ApiKeySecretName, request.ApiKey, cancellationToken);
            }
        }

        var now = DateTimeOffset.UtcNow;
        profile.Name = request.Name.Trim();
        profile.ProviderType = request.ProviderType;
        profile.BaseUrl = request.BaseUrl.Trim();
        profile.DefaultChatModel = request.DefaultChatModel.Trim();
        profile.DefaultEmbeddingModel = NormalizeOptional(request.DefaultEmbeddingModel);
        profile.ContextWindowTokens = request.ContextWindowTokens;
        profile.MaxOutputTokens = request.MaxOutputTokens;
        if (request.MaximumConcurrentRequests is { } maximumConcurrentRequests)
            profile.MaximumConcurrentRequests = maximumConcurrentRequests;
        profile.SupportsStreaming = request.SupportsStreaming;
        profile.SupportsToolCalling = request.SupportsToolCalling;
        profile.SupportsStructuredOutput = request.SupportsStructuredOutput;
        profile.SupportsVision = request.SupportsVision;
        profile.IsEnabled = request.IsEnabled;
        profile.UpdatedAt = now;

        if (connectionChanged)
        {
            profile.LastSuccessfulConnectionAt = null;
        }

        if (!profile.IsEnabled)
        {
            await ClearDefaultReferencesAsync(profile.Id, cancellationToken);
        }

        _dbContext.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "llm_provider_profile.updated",
            EntityType = nameof(LlmProviderProfile),
            EntityId = profile.Id,
            Summary = $"LLM provider profile updated: {profile.Name}",
            CreatedAt = now
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new LlmProviderProfileActionResponse(true, null, null, profile.ToResponse());
    }

    public async Task<LlmProviderProfileActionResponse> DeleteAsync(
        Guid providerProfileId,
        CancellationToken cancellationToken = default)
    {
        var profile = await _dbContext.LlmProviderProfiles
            .SingleOrDefaultAsync(x => x.Id == providerProfileId, cancellationToken);

        if (profile is null)
        {
            return Failure("provider_profile_not_found", "Provider profile was not found.");
        }

        var now = DateTimeOffset.UtcNow;
        await ClearDefaultReferencesAsync(profile.Id, cancellationToken);

        if (!string.IsNullOrWhiteSpace(profile.ApiKeySecretName))
        {
            await _secretStore.DeleteAsync(profile.ApiKeySecretName, cancellationToken);
        }

        var tests = await _dbContext.ModelCapabilityTests
            .Where(x => x.ProviderProfileId == profile.Id)
            .ToListAsync(cancellationToken);

        _dbContext.ModelCapabilityTests.RemoveRange(tests);
        _dbContext.LlmProviderProfiles.Remove(profile);
        _dbContext.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "llm_provider_profile.deleted",
            EntityType = nameof(LlmProviderProfile),
            EntityId = profile.Id,
            Summary = $"LLM provider profile deleted: {profile.Name}",
            CreatedAt = now
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new LlmProviderProfileActionResponse(true, null, "Provider profile deleted.");
    }

    public async Task<IReadOnlyList<LlmProviderProfileResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await _dbContext.LlmProviderProfiles
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        return profiles.Select(x => x.ToResponse()).ToList();
    }

    public async Task<LlmProviderProfileResponse?> GetAsync(
        Guid providerProfileId,
        CancellationToken cancellationToken = default)
    {
        var profile = await _dbContext.LlmProviderProfiles
            .SingleOrDefaultAsync(x => x.Id == providerProfileId, cancellationToken);

        return profile?.ToResponse();
    }

    public Task<ModelCapabilityTestResult> TestAsync(
        Guid providerProfileId,
        CancellationToken cancellationToken = default)
    {
        return _connectionTester.TestAsync(providerProfileId, cancellationToken);
    }

    public async Task<LlmProviderProfileActionResponse> SetDefaultChatProviderAsync(
        Guid providerProfileId,
        CancellationToken cancellationToken = default)
    {
        var profile = await _dbContext.LlmProviderProfiles
            .SingleOrDefaultAsync(x => x.Id == providerProfileId, cancellationToken);

        if (profile is null)
        {
            return Failure("provider_profile_not_found", "Provider profile was not found.");
        }

        if (!profile.IsEnabled)
        {
            return Failure("provider_profile_disabled", "Provider profile must be enabled before it can be selected.");
        }

        var hasSuccessfulChatTest = await _dbContext.ModelCapabilityTests
            .AnyAsync(x => x.ProviderProfileId == providerProfileId && x.ChatSucceeded, cancellationToken);

        if (!hasSuccessfulChatTest)
        {
            return Failure("successful_chat_test_required", "Provider profile must have a successful chat capability test.");
        }

        var configuration = await _dbContext.SystemConfigurations
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        if (configuration is null)
        {
            configuration = new SystemConfiguration
            {
                Id = Guid.NewGuid(),
                IsFirstRunComplete = false,
                CreatedAt = now,
                UpdatedAt = now
            };

            _dbContext.SystemConfigurations.Add(configuration);
        }

        configuration.DefaultChatProviderId = providerProfileId;
        configuration.UpdatedAt = now;

        _dbContext.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "setup.default_chat_provider.selected",
            EntityType = nameof(LlmProviderProfile),
            EntityId = providerProfileId,
            Summary = $"Default chat provider selected: {profile.Name}",
            CreatedAt = now
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new LlmProviderProfileActionResponse(true, null, null, profile.ToResponse());
    }

    private static LlmProviderProfileActionResponse Failure(string errorCode, string message)
    {
        return new LlmProviderProfileActionResponse(false, errorCode, message);
    }

    private async Task ClearDefaultReferencesAsync(
        Guid providerProfileId,
        CancellationToken cancellationToken)
    {
        var configurations = await _dbContext.SystemConfigurations
            .Where(x => x.DefaultChatProviderId == providerProfileId || x.DefaultEmbeddingProviderId == providerProfileId)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        foreach (var configuration in configurations)
        {
            if (configuration.DefaultChatProviderId == providerProfileId)
            {
                configuration.DefaultChatProviderId = null;
            }

            if (configuration.DefaultEmbeddingProviderId == providerProfileId)
            {
                configuration.DefaultEmbeddingProviderId = null;
            }

            configuration.UpdatedAt = now;
        }
    }

    private static LlmProviderProfileActionResponse? Validate(
        string name,
        string baseUrl,
        int? maximumConcurrentRequests)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Failure("validation_error", "Provider name is required.");
        }

        if (!IsValidBaseUrl(baseUrl))
        {
            return Failure("invalid_base_url", "Provider base URL must be an absolute HTTP or HTTPS URL.");
        }

        if (maximumConcurrentRequests is < 1)
        {
            return Failure("validation_error", "Maximum concurrent requests must be at least 1.");
        }

        return null;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static bool IsValidBaseUrl(string baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https";
    }
}
