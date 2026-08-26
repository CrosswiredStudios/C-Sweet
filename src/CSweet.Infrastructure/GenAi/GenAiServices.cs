using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using CSweet.AI.Providers;
using CSweet.Application.GenAi;
using CSweet.Contracts.GenAi;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CSweet.Infrastructure.GenAi;

public sealed class GenAiProviderProfileService(
    CSweetDbContext db,
    ILlmProviderSecretStore secrets,
    IEnumerable<IGenAiProviderAdapter> adapters) : IGenAiProviderProfileService
{
    private readonly IReadOnlyDictionary<GenAiProviderType, IGenAiProviderAdapter> _adapters =
        adapters.ToDictionary(x => x.ProviderType);

    public async Task<IReadOnlyList<GenAiProviderProfileResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await db.GenAiProviderProfiles.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync(cancellationToken);
        var operations = await db.GenAiOperationConfigurations.AsNoTracking().ToListAsync(cancellationToken);
        var defaults = await db.GenAiOperationDefaults.AsNoTracking().Select(x => x.OperationConfigurationId).ToListAsync(cancellationToken);
        return profiles.Select(x => ToResponse(x, operations.Where(y => y.ProviderProfileId == x.Id), defaults)).ToList();
    }

    public async Task<GenAiProviderProfileResponse?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await db.GenAiProviderProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (profile is null) return null;
        var operations = await db.GenAiOperationConfigurations.AsNoTracking().Where(x => x.ProviderProfileId == id).ToListAsync(cancellationToken);
        var defaults = await db.GenAiOperationDefaults.AsNoTracking().Select(x => x.OperationConfigurationId).ToListAsync(cancellationToken);
        return ToResponse(profile, operations, defaults);
    }

    public async Task<GenAiActionResponse> CreateAsync(CreateGenAiProviderProfileRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateProfile(request.Name, request.BaseUrl);
        if (validation is not null) return validation;
        var connectionTest = await TestDraftAsync(new(
            null,
            request.ProviderType,
            request.BaseUrl,
            request.ApiKey), cancellationToken);
        if (!connectionTest.Succeeded)
            return Failure(connectionTest.ErrorCode ?? "connection_test_failed", connectionTest.Message);

        var now = DateTimeOffset.UtcNow;
        var profile = new GenAiProviderProfile
        {
            Id = Guid.NewGuid(), Name = request.Name.Trim(), ProviderType = request.ProviderType,
            BaseUrl = request.BaseUrl.TrimEnd('/'), IsEnabled = true,
            LastSuccessfulConnectionAt = connectionTest.TestedAt, CreatedAt = now, UpdatedAt = now
        };
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            profile.ApiKeySecretName = $"genai-provider-profiles/{profile.Id}/api-key";
            await secrets.StoreAsync(profile.ApiKeySecretName, request.ApiKey.Trim(), cancellationToken);
        }
        db.GenAiProviderProfiles.Add(profile);
        AddAudit(db, "genai_provider_profile.created", nameof(GenAiProviderProfile), profile.Id, $"GenAI provider profile created: {profile.Name}");
        await db.SaveChangesAsync(cancellationToken);
        return new(true, null, "GenAI provider saved.", await GetAsync(profile.Id, cancellationToken));
    }

    public async Task<GenAiActionResponse> UpdateAsync(Guid id, UpdateGenAiProviderProfileRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateProfile(request.Name, request.BaseUrl);
        if (validation is not null) return validation;
        var profile = await db.GenAiProviderProfiles.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (profile is null) return Failure("provider_not_found", "GenAI provider profile was not found.");
        var normalizedBaseUrl = request.BaseUrl.TrimEnd('/');
        var connectionChanged = profile.ProviderType != request.ProviderType ||
            !string.Equals(profile.BaseUrl.TrimEnd('/'), normalizedBaseUrl, StringComparison.OrdinalIgnoreCase) ||
            request.ReplaceApiKey;
        GenAiConnectionTestResponse? connectionTest = null;
        if (connectionChanged)
        {
            connectionTest = await TestDraftAsync(new(
                id,
                request.ProviderType,
                normalizedBaseUrl,
                request.ApiKey,
                request.ReplaceApiKey), cancellationToken);
            if (!connectionTest.Succeeded)
                return Failure(connectionTest.ErrorCode ?? "connection_test_failed", connectionTest.Message);
        }

        if (request.ReplaceApiKey)
        {
            if (string.IsNullOrWhiteSpace(request.ApiKey))
            {
                if (profile.ApiKeySecretName is not null) await secrets.DeleteAsync(profile.ApiKeySecretName, cancellationToken);
                profile.ApiKeySecretName = null;
            }
            else
            {
                profile.ApiKeySecretName ??= $"genai-provider-profiles/{profile.Id}/api-key";
                await secrets.StoreAsync(profile.ApiKeySecretName, request.ApiKey.Trim(), cancellationToken);
            }
        }
        profile.Name = request.Name.Trim();
        profile.ProviderType = request.ProviderType;
        profile.BaseUrl = normalizedBaseUrl;
        profile.IsEnabled = request.IsEnabled;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        if (connectionTest is not null)
            profile.LastSuccessfulConnectionAt = connectionTest.TestedAt;
        AddAudit(db, "genai_provider_profile.updated", nameof(GenAiProviderProfile), profile.Id, $"GenAI provider profile updated: {profile.Name}");
        await db.SaveChangesAsync(cancellationToken);
        return new(true, null, "GenAI provider updated.", await GetAsync(profile.Id, cancellationToken));
    }

    public async Task<GenAiActionResponse> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await db.GenAiProviderProfiles.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (profile is null) return Failure("provider_not_found", "GenAI provider profile was not found.");
        if (await db.GenAiJobs.AnyAsync(x => x.OperationConfiguration!.ProviderProfileId == id, cancellationToken))
            return Failure("provider_in_use", "Provider history exists. Disable the provider instead of deleting it.");
        if (profile.ApiKeySecretName is not null) await secrets.DeleteAsync(profile.ApiKeySecretName, cancellationToken);
        db.GenAiProviderProfiles.Remove(profile);
        AddAudit(db, "genai_provider_profile.deleted", nameof(GenAiProviderProfile), profile.Id, $"GenAI provider profile deleted: {profile.Name}");
        await db.SaveChangesAsync(cancellationToken);
        return new(true, null, "GenAI provider deleted.");
    }

    public async Task<GenAiConnectionTestResponse> TestDraftAsync(
        TestGenAiProviderConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new(false, "invalid_base_url", "Provider base URL must be an absolute HTTP or HTTPS URL.", DateTimeOffset.UtcNow);
        if (!_adapters.TryGetValue(request.ProviderType, out var adapter))
            return new(false, "adapter_not_found", "No adapter is registered for this provider.", DateTimeOffset.UtcNow);

        string? apiKey;
        if (request.ProviderProfileId is { } providerProfileId && !request.ReplaceApiKey)
        {
            var existing = await db.GenAiProviderProfiles.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == providerProfileId, cancellationToken);
            if (existing is null)
                return new(false, "provider_not_found", "GenAI provider profile was not found.", DateTimeOffset.UtcNow);
            apiKey = existing.ApiKeySecretName is null
                ? null
                : await secrets.GetAsync(existing.ApiKeySecretName, cancellationToken);
        }
        else
        {
            apiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim();
        }

        var profile = new GenAiProviderProfile
        {
            Id = request.ProviderProfileId ?? Guid.NewGuid(),
            Name = request.ProviderType.ToString(),
            ProviderType = request.ProviderType,
            BaseUrl = request.BaseUrl.TrimEnd('/'),
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return await adapter.TestAsync(profile, apiKey, cancellationToken);
    }

    public async Task<GenAiConnectionTestResponse> TestAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await db.GenAiProviderProfiles.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (profile is null) return new(false, "provider_not_found", "GenAI provider profile was not found.", DateTimeOffset.UtcNow);
        if (!_adapters.TryGetValue(profile.ProviderType, out var adapter))
            return new(false, "adapter_not_found", "No adapter is registered for this provider.", DateTimeOffset.UtcNow);
        var key = profile.ApiKeySecretName is null ? null : await secrets.GetAsync(profile.ApiKeySecretName, cancellationToken);
        var result = await adapter.TestAsync(profile, key, cancellationToken);
        if (result.Succeeded)
        {
            profile.LastSuccessfulConnectionAt = result.TestedAt;
            profile.UpdatedAt = result.TestedAt;
            await db.SaveChangesAsync(cancellationToken);
        }
        return result;
    }

    public async Task<GenAiActionResponse> SaveOperationAsync(Guid providerId, Guid? operationId, SaveGenAiOperationConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        var provider = await db.GenAiProviderProfiles.SingleOrDefaultAsync(x => x.Id == providerId, cancellationToken);
        if (provider is null) return Failure("provider_not_found", "GenAI provider profile was not found.");
        if (string.IsNullOrWhiteSpace(request.Name)) return Failure("validation_error", "Operation name is required.");
        if (!TryJson(request.TemplateJson) || !TryJson(request.DefaultsJson))
            return Failure("invalid_json", "Workflow/template and defaults must be valid JSON.");
        if (!_adapters.TryGetValue(provider.ProviderType, out var adapter))
            return Failure("adapter_not_found", "No adapter is registered for this provider.");

        var operation = operationId.HasValue
            ? await db.GenAiOperationConfigurations.SingleOrDefaultAsync(x => x.Id == operationId && x.ProviderProfileId == providerId, cancellationToken)
            : null;
        if (operationId.HasValue && operation is null) return Failure("operation_not_found", "Operation configuration was not found.");
        var now = DateTimeOffset.UtcNow;
        operation ??= new GenAiOperationConfiguration { Id = Guid.NewGuid(), ProviderProfileId = providerId, CreatedAt = now };
        operation.OperationType = request.OperationType;
        operation.Name = request.Name.Trim();
        operation.ModelId = Normalize(request.ModelId);
        operation.TemplateJson = Normalize(request.TemplateJson);
        operation.OutputSelector = Normalize(request.OutputSelector);
        operation.DefaultsJson = Normalize(request.DefaultsJson);
        operation.IsEnabled = request.IsEnabled;
        operation.UpdatedAt = now;
        try
        {
            await adapter.ValidateOperationAsync(provider, operation, cancellationToken);
            operation.LastValidatedAt = now;
        }
        catch (InvalidOperationException ex)
        {
            return Failure("operation_invalid", ex.Message);
        }
        if (operationId is null) db.GenAiOperationConfigurations.Add(operation);
        await db.SaveChangesAsync(cancellationToken);
        if (!await db.GenAiOperationDefaults.AnyAsync(x => x.OperationType == operation.OperationType, cancellationToken))
        {
            db.GenAiOperationDefaults.Add(new GenAiOperationDefault
            {
                Id = Guid.NewGuid(), OperationType = operation.OperationType,
                OperationConfigurationId = operation.Id, UpdatedAt = now
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        return new(true, null, "Operation configuration saved.", Operation: await OperationResponseAsync(operation.Id, cancellationToken));
    }

    public async Task<GenAiActionResponse> DeleteOperationAsync(Guid providerId, Guid operationId, CancellationToken cancellationToken = default)
    {
        var operation = await db.GenAiOperationConfigurations.SingleOrDefaultAsync(x => x.Id == operationId && x.ProviderProfileId == providerId, cancellationToken);
        if (operation is null) return Failure("operation_not_found", "Operation configuration was not found.");
        if (await db.GenAiJobs.AnyAsync(x => x.OperationConfigurationId == operationId, cancellationToken))
            return Failure("operation_in_use", "Operation history exists. Disable this operation instead of deleting it.");
        db.GenAiOperationConfigurations.Remove(operation);
        await db.SaveChangesAsync(cancellationToken);
        return new(true, null, "Operation configuration deleted.");
    }

    public async Task<GenAiActionResponse> SetDefaultAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        var operation = await db.GenAiOperationConfigurations.Include(x => x.ProviderProfile)
            .SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);
        if (operation?.IsEnabled != true || operation.ProviderProfile?.IsEnabled != true)
            return Failure("operation_unavailable", "The operation and provider must be enabled.");
        var existing = await db.GenAiOperationDefaults.SingleOrDefaultAsync(x => x.OperationType == operation.OperationType, cancellationToken);
        if (existing is null)
            db.GenAiOperationDefaults.Add(new GenAiOperationDefault { Id = Guid.NewGuid(), OperationType = operation.OperationType, OperationConfigurationId = operation.Id, UpdatedAt = DateTimeOffset.UtcNow });
        else
        {
            existing.OperationConfigurationId = operation.Id;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        return new(true, null, "Default operation updated.", Operation: await OperationResponseAsync(operation.Id, cancellationToken));
    }

    private async Task<GenAiOperationConfigurationResponse> OperationResponseAsync(Guid id, CancellationToken token)
    {
        var x = await db.GenAiOperationConfigurations.AsNoTracking().SingleAsync(y => y.Id == id, token);
        var isDefault = await db.GenAiOperationDefaults.AnyAsync(y => y.OperationConfigurationId == id, token);
        return ToResponse(x, isDefault);
    }

    private static GenAiProviderProfileResponse ToResponse(GenAiProviderProfile x, IEnumerable<GenAiOperationConfiguration> operations, ICollection<Guid> defaults) =>
        new(x.Id, x.Name, x.ProviderType, x.BaseUrl, x.ApiKeySecretName is not null, x.IsEnabled,
            x.LastSuccessfulConnectionAt, x.CreatedAt, x.UpdatedAt,
            operations.OrderBy(y => y.OperationType).ThenBy(y => y.Name).Select(y => ToResponse(y, defaults.Contains(y.Id))).ToList());

    internal static GenAiOperationConfigurationResponse ToResponse(GenAiOperationConfiguration x, bool isDefault) =>
        new(x.Id, x.ProviderProfileId, x.OperationType, x.Name, x.ModelId, x.TemplateJson, x.OutputSelector,
            x.DefaultsJson, x.IsEnabled, isDefault, x.LastValidatedAt);

    private static GenAiActionResponse? ValidateProfile(string name, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(name)) return Failure("validation_error", "Provider name is required.");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Failure("invalid_base_url", "Provider base URL must be an absolute HTTP or HTTPS URL.");
        return null;
    }

    private static bool TryJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        try { using var _ = JsonDocument.Parse(value); return true; } catch (JsonException) { return false; }
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static GenAiActionResponse Failure(string code, string message) => new(false, code, message);
    private static void AddAudit(CSweetDbContext dbContext, string eventType, string entityType, Guid entityId, string summary) =>
        dbContext.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), EventType = eventType, EntityType = entityType, EntityId = entityId, Summary = summary, CreatedAt = DateTimeOffset.UtcNow });
}

public sealed class MediaAssetStorageOptions
{
    public const string SectionName = "CSweet:MediaAssets";
    public const long AbsoluteMaximumFileSizeBytes = 256L * 1024 * 1024 * 1024;
    public long MaximumFileSizeBytes { get; set; } = 1024L * 1024 * 1024;
    public long MaximumOrganizationStorageBytes { get; set; } = 512L * 1024 * 1024 * 1024;
    public int ResumableChunkSizeBytes { get; set; } = 8 * 1024 * 1024;
    public int UploadSessionLifetimeHours { get; set; } = 24;
}

public sealed class FileMediaAssetStore : IMediaAssetStore
{
    private readonly string _root;
    private readonly long _maximumFileSizeBytes;
    public FileMediaAssetStore(IConfiguration configuration, IOptions<MediaAssetStorageOptions> options)
    {
        _root = Path.GetFullPath(configuration["CSweet:GenAi:MediaRoot"] ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSweet", "media"));
        Directory.CreateDirectory(_root);
        _maximumFileSizeBytes = Math.Clamp(options.Value.MaximumFileSizeBytes, 1, MediaAssetStorageOptions.AbsoluteMaximumFileSizeBytes);
    }

    public async Task<(string StorageKey, long SizeBytes, string Sha256)> SaveAsync(string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(Path.GetFileName(fileName));
        var key = $"{DateTimeOffset.UtcNow:yyyy/MM}/{Guid.NewGuid():N}{extension}";
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > _maximumFileSizeBytes)
                throw new InvalidOperationException($"Media asset exceeds the deployment limit of {_maximumFileSizeBytes} bytes.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return (key, total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new FileStream(Resolve(storageKey), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true));

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var path = Resolve(storageKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string Resolve(string key)
    {
        var path = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid media storage key.");
        return path;
    }
}

public sealed class MediaAssetService(
    CSweetDbContext db,
    IMediaAssetStore store,
    IOptions<MediaAssetStorageOptions>? configuredOptions = null) : IMediaAssetService
{
    private readonly long _organizationQuota = Math.Max(1,
        configuredOptions?.Value.MaximumOrganizationStorageBytes ?? 512L * 1024 * 1024 * 1024);

    public async Task<MediaAssetResponse> SaveUploadAsync(Guid organizationId, string fileName, string contentType, Stream content, CancellationToken cancellationToken = default)
    {
        if (!await db.CoreOrganizations.AnyAsync(x => x.Id == organizationId, cancellationToken))
            throw new InvalidOperationException("Organization was not found.");
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName)) throw new InvalidOperationException("A file name is required.");
        var storedBytes = await db.MediaAssets.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .SumAsync(x => (long?)x.SizeBytes, cancellationToken) ?? 0;
        if (content.CanSeek && (content.Length - content.Position > _organizationQuota - storedBytes))
            throw new InvalidOperationException("The organization's media storage quota would be exceeded.");
        var normalizedType = NormalizeContentType(contentType);
        await ValidateSignatureAsync(content, normalizedType, cancellationToken);
        if (content.CanSeek) content.Position = 0;
        var saved = await store.SaveAsync(safeName, content, cancellationToken);
        if (saved.SizeBytes > _organizationQuota - storedBytes)
        {
            await store.DeleteAsync(saved.StorageKey, cancellationToken);
            throw new InvalidOperationException("The organization's media storage quota would be exceeded.");
        }
        var entity = new MediaAsset
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, FileName = safeName, ContentType = normalizedType,
            SizeBytes = saved.SizeBytes, Sha256 = saved.Sha256, StorageKey = saved.StorageKey, CreatedAt = DateTimeOffset.UtcNow
        };
        db.MediaAssets.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(entity);
    }

    public async Task<MediaAssetResponse?> GetAsync(Guid id, Guid organizationId, CancellationToken cancellationToken = default) =>
        (await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId, cancellationToken)) is { } x ? ToResponse(x) : null;

    public async Task<(MediaAssetResponse Asset, Stream Content)?> OpenReadAsync(Guid id, Guid organizationId, CancellationToken cancellationToken = default)
    {
        var asset = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId, cancellationToken);
        return asset is null ? null : (ToResponse(asset), await store.OpenReadAsync(asset.StorageKey, cancellationToken));
    }

    public async Task DeleteAsync(Guid id, Guid organizationId, CancellationToken cancellationToken = default)
    {
        var asset = await db.MediaAssets.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId, cancellationToken);
        if (asset is null) return;
        if (await db.ConversationMessageAttachments.AsNoTracking()
            .AnyAsync(x => x.MediaAssetId == id && x.OrganizationId == organizationId, cancellationToken))
            throw new InvalidOperationException("Media attached to retained conversation history cannot be deleted.");
        await store.DeleteAsync(asset.StorageKey, cancellationToken);
        db.MediaAssets.Remove(asset);
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static MediaAssetResponse ToResponse(MediaAsset x) =>
        new(x.Id, x.FileName, x.ContentType, x.SizeBytes, x.Sha256, x.Width, x.Height, x.DurationSeconds, x.CreatedAt);

    internal static string NormalizeContentType(string value) => value.Split(';', 2)[0].Trim().ToLowerInvariant() switch
    {
        "image/png" => "image/png", "image/jpeg" => "image/jpeg", "image/webp" => "image/webp",
        "video/mp4" => "video/mp4", "video/webm" => "video/webm",
        "text/vtt" => "text/vtt", "application/x-subrip" => "application/x-subrip",
        "text/plain" => "text/plain", "text/markdown" or "text/x-markdown" => "text/markdown",
        "application/pdf" => "application/pdf",
        _ => throw new InvalidOperationException("Only PNG, JPEG, WebP, PDF, UTF-8 text, Markdown, MP4, WebM, WebVTT, and SubRip media are supported.")
    };

    internal static async Task ValidateSignatureAsync(Stream stream, string contentType, CancellationToken token)
    {
        if (!stream.CanSeek) return;
        var header = new byte[4096];
        var read = await stream.ReadAsync(header, token);
        stream.Position = 0;
        var valid = contentType switch
        {
            "image/png" => read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/jpeg" => read >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff,
            "image/webp" => read >= 12 && Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "WEBP",
            "application/pdf" => read >= 5 && Encoding.ASCII.GetString(header, 0, 5) == "%PDF-",
            "video/mp4" => read >= 12 && Encoding.ASCII.GetString(header, 4, 4) == "ftyp",
            "video/webm" => read >= 4 && header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }),
            "text/vtt" => read >= 6 && Encoding.UTF8.GetString(header, 0, read).TrimStart('\uFEFF').StartsWith("WEBVTT", StringComparison.Ordinal),
            "application/x-subrip" or "text/plain" or "text/markdown" =>
                read > 0 && !header.AsSpan(0, read).Contains((byte)0) && IsValidUtf8(header.AsSpan(0, read)),
            _ => false
        };
        if (!valid) throw new InvalidOperationException("The media content does not match its declared type.");
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

public sealed class GenAiJobService(
    CSweetDbContext db,
    IEnumerable<IGenAiProviderAdapter>? adapters = null,
    ILlmProviderSecretStore? secretStore = null) : IGenAiJobService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<GenAiProviderType, IGenAiProviderAdapter> _adapters =
        (adapters ?? []).ToDictionary(x => x.ProviderType);

    public async Task<GenAiJobResponse> StartAsync(Guid organizationId, Guid installationId, GenAiOperationType operationType, GenAiMediaRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new InvalidOperationException("A prompt is required.");
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var previous = await db.GenAiJobs.SingleOrDefaultAsync(x => x.AgentInstallationId == installationId &&
                x.OperationType == operationType && x.IdempotencyKey == request.IdempotencyKey.Trim(), cancellationToken);
            if (previous is not null) return await ToResponseAsync(previous, cancellationToken);
        }
        Guid operationId;
        if (request.OperationConfigurationId.HasValue) operationId = request.OperationConfigurationId.Value;
        else
        {
            operationId = await db.GenAiOperationDefaults.Where(x => x.OperationType == operationType)
                .Select(x => x.OperationConfigurationId).SingleOrDefaultAsync(cancellationToken);
            if (operationId == Guid.Empty) throw new InvalidOperationException($"No default {operationType} operation is configured.");
        }
        var operation = await db.GenAiOperationConfigurations.Include(x => x.ProviderProfile)
            .SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);
        if (operation?.OperationType != operationType || operation.IsEnabled != true || operation.ProviderProfile?.IsEnabled != true)
            throw new InvalidOperationException("The requested GenAI operation is unavailable.");
        var sourceIds = request.SourceAssetIds ?? [];
        if (operationType is GenAiOperationType.ImageEditing or GenAiOperationType.VideoEditing && sourceIds.Count == 0)
            throw new InvalidOperationException("Editing requires at least one source media asset.");
        var allIds = sourceIds.Concat(request.MaskAssetId is null ? [] : [request.MaskAssetId.Value]).Distinct().ToList();
        if (allIds.Count > 0 && await db.MediaAssets.CountAsync(x => allIds.Contains(x.Id) && x.OrganizationId == organizationId, cancellationToken) != allIds.Count)
            throw new InvalidOperationException("One or more media assets are unavailable to this organization.");
        var now = DateTimeOffset.UtcNow;
        var job = new GenAiJob
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = installationId,
            OperationConfigurationId = operation.Id, OperationType = operationType, Status = GenAiJobStatus.Queued,
            PromptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Prompt))).ToLowerInvariant(),
            RequestJson = JsonSerializer.Serialize(request, JsonOptions), IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim(),
            CreatedAt = now, UpdatedAt = now
        };
        db.GenAiJobs.Add(job);
        AddAudit(job, "genai.job.queued", "GenAI media job queued.");
        await db.SaveChangesAsync(cancellationToken);
        return await ToResponseAsync(job, cancellationToken);
    }

    public async Task<GenAiJobResponse?> GetAsync(Guid jobId, Guid organizationId, Guid? installationId = null, CancellationToken cancellationToken = default)
    {
        var job = await db.GenAiJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId && x.OrganizationId == organizationId &&
            (!installationId.HasValue || x.AgentInstallationId == installationId.Value), cancellationToken);
        return job is null ? null : await ToResponseAsync(job, cancellationToken);
    }

    public async Task<GenAiJobResponse?> CancelAsync(Guid jobId, Guid organizationId, Guid? installationId = null, CancellationToken cancellationToken = default)
    {
        var job = await db.GenAiJobs.Include(x => x.OperationConfiguration)!.ThenInclude(x => x!.ProviderProfile)
            .SingleOrDefaultAsync(x => x.Id == jobId && x.OrganizationId == organizationId &&
            (!installationId.HasValue || x.AgentInstallationId == installationId.Value), cancellationToken);
        if (job is null) return null;
        if (job.Status is GenAiJobStatus.Queued or GenAiJobStatus.Running)
        {
            job.Status = GenAiJobStatus.Canceled;
            job.CompletedAt = job.UpdatedAt = DateTimeOffset.UtcNow;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            AddAudit(job, "genai.job.canceled", "GenAI media job canceled.");
            await db.SaveChangesAsync(cancellationToken);
            var profile = job.OperationConfiguration?.ProviderProfile;
            if (profile is not null && !string.IsNullOrWhiteSpace(job.ProviderJobId) &&
                _adapters.TryGetValue(profile.ProviderType, out var adapter))
            {
                try
                {
                    var key = profile.ApiKeySecretName is null || secretStore is null
                        ? null
                        : await secretStore.GetAsync(profile.ApiKeySecretName, cancellationToken);
                    await adapter.CancelAsync(profile, job.ProviderJobId, key, cancellationToken);
                }
                catch
                {
                    // The durable canceled state is authoritative even if the provider no longer accepts cancellation.
                }
            }
        }
        return await ToResponseAsync(job, cancellationToken);
    }

    private async Task<GenAiJobResponse> ToResponseAsync(GenAiJob job, CancellationToken token)
    {
        var assets = await db.MediaAssets.AsNoTracking().Where(x => x.GenAiJobId == job.Id).OrderBy(x => x.CreatedAt).ToListAsync(token);
        return new(job.Id, job.OperationType, job.Status, job.ErrorCode, job.ErrorMessage, job.CreatedAt, job.UpdatedAt,
            assets.Select(MediaAssetService.ToResponse).ToList());
    }

    private void AddAudit(GenAiJob job, string eventType, string summary) =>
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), OrganizationId = job.OrganizationId, EventType = eventType,
            EntityType = nameof(GenAiJob), EntityId = job.Id, Summary = summary, CreatedAt = DateTimeOffset.UtcNow
        });
}
