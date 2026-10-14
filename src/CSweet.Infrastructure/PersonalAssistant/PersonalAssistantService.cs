using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.PersonalAssistant;
using CSweet.Application.Setup;
using CSweet.Contracts.PersonalAssistant;
using CSweet.Contracts.Realtime;
using CSweet.Contracts.Plugins;
using CSweet.Domain.PersonalAssistant;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.PersonalAssistant;

public sealed class PersonalAssistantService(CSweetDbContext db, BundledPersonalAssistantInstaller installer,
    IAgentInstallationConfigurationService configurations, IAgentConfigurationService effectiveConfigurations,
    AgentWorkInbox inbox, IPersonalAssistantRuntimeScheduler scheduler) : IPersonalAssistantService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim ConfigurationGate = new(1, 1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> MessageGates = new();

    public async Task<PersonalAssistantSettings> GetSettingsAsync(Guid userId, CancellationToken token = default)
    {
        await RequireUserAsync(userId, token);
        var profile = await db.PersonalAssistantProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.ApplicationUserId == userId, token);
        if (profile is null)
        {
            var defaults = await db.SystemConfigurations.AsNoTracking().FirstOrDefaultAsync(token);
            return new(null, defaults?.PersonalAssistantProviderId, defaults?.PersonalAssistantModel, 0, false,
                "Choose a provider and model to enable your personal assistant.", BundledPersonalAssistantInstaller.ReadManifest().Configuration, new Dictionary<string, JsonElement>());
        }
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion)
            .Include(x => x.Configuration).SingleAsync(x => x.Id == profile.AgentInstallationId, token);
        var config = await effectiveConfigurations.ResolveInstallationAsync(installation.Id, token);
        var provider = config.Settings.TryGetValue("llmProviderId", out var p) && Guid.TryParse(p.GetString(), out var id) ? (Guid?)id : null;
        var model = config.Settings.TryGetValue("llmModel", out var m) ? m.GetString() : null;
        var ready = installation.PackageVersion?.Status is not (AgentPackageVersionStatus.Failed or AgentPackageVersionStatus.Revoked) &&
            await UserScopedAgentPolicy.IsAuthorizedAsync(db, installation.Id, installation.BusinessId, token) && provider.HasValue && !string.IsNullOrWhiteSpace(model) &&
            await db.LlmProviderProfiles.AnyAsync(x => x.Id == provider && x.IsEnabled, token);
        var detail = installation.PackageVersion?.Status is AgentPackageVersionStatus.Failed or AgentPackageVersionStatus.Revoked
            ? "The assistant package is unavailable. Ask the installation administrator to review its build status."
            : installation.PackageVersion?.Status == AgentPackageVersionStatus.Built
            ? ready ? null : "Your selected provider is unavailable. Update the assistant's settings."
            : "The personal assistant package is building. Your queued work will resume when it is ready.";
        var manifest = JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion!.ManifestJson, Json)!;
        return new(installation.Id, provider, model, installation.Configuration?.Revision ?? config.Revision,
            ready, detail, manifest.Configuration, config.Settings, profile.Name);
    }

    public async Task<PersonalAssistantSettings> ConfigureAsync(Guid userId, ConfigurePersonalAssistantRequest request, CancellationToken token = default)
    {
        await RequireUserAsync(userId, token);
        if (request.ProviderId == Guid.Empty || string.IsNullOrWhiteSpace(request.Model) || request.Model.Length > 256)
            throw new ArgumentException("Choose a provider and model for your assistant.");
        if (request.Name is not null && (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100))
            throw new ArgumentException("Enter an assistant name between 1 and 100 characters.");
        if (request.SetInstallationDefault && !await db.Users.AnyAsync(x => x.Id == userId && x.IsInitialAdministrator, token))
            throw new UnauthorizedAccessException("Only the installation owner may change the first-run assistant defaults.");
        var settings = new Dictionary<string, JsonElement>
        {
            ["llmProviderId"] = JsonSerializer.SerializeToElement(request.ProviderId.ToString("D")),
            ["llmModel"] = JsonSerializer.SerializeToElement(request.Model.Trim()),
            ["maxContextWindowTokens"] = JsonSerializer.SerializeToElement(request.MaxContextWindowTokens),
            ["maxOutputTokens"] = JsonSerializer.SerializeToElement(request.MaxOutputTokens)
        };
        await ConfigurationGate.WaitAsync(token);
        try
        {
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"personal-assistant-config:" + userId.ToString("D")}, 0))", token);
            var before = await GetSettingsAsync(userId, token);
            if (request.ExpectedRevision.HasValue && request.ExpectedRevision != before.Revision)
                throw new DbUpdateConcurrencyException("The assistant's configuration changed. Reload before saving.");
            var installationId = await installer.EnsureAsync(userId, settings, token);
            await configurations.SaveAsync(installationId, "1.0", settings, token);
            var profile = await db.PersonalAssistantProfiles.SingleAsync(x => x.ApplicationUserId == userId, token);
            if (request.Name is not null && profile.Name != request.Name.Trim())
            {
                profile.Name = request.Name.Trim();
                // Name-only edits participate in the same optimistic revision check as model edits.
                var configuration = await db.AgentInstallationConfigurations.SingleAsync(x => x.AgentInstallationId == installationId, token);
                if (configuration.Revision == before.Revision) configuration.Revision++;
                configuration.UpdatedAt = DateTimeOffset.UtcNow;
            }
            if (request.SetInstallationDefault)
            {
                var system = await db.SystemConfigurations.FirstAsync(token);
                system.PersonalAssistantProviderId = request.ProviderId;
                system.PersonalAssistantModel = request.Model.Trim();
            }
            PersonalAssistantRealtime.Queue(db, userId, AppRealtimeEvents.PersonalAssistantChanged, installationId);
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return await GetSettingsAsync(userId, token);
        }
        finally { ConfigurationGate.Release(); }
    }

    public async Task<Guid> RequireInstallationAsync(Guid userId, CancellationToken token = default)
    {
        var settings = await GetSettingsAsync(userId, token);
        if (!settings.Ready || settings.InstallationId is null)
            throw new InvalidOperationException(settings.Detail ?? "Configure your personal assistant first.");
        return settings.InstallationId.Value;
    }

    public async Task<IReadOnlyList<PersonalAssistantMessageResponse>> ListMessagesAsync(Guid userId, Guid? draftId = null, CancellationToken token = default)
    {
        await RequireUserAsync(userId, token);
        if (draftId.HasValue) await RequireDraftAsync(userId, draftId.Value, token);
        var rows = await db.PersonalAssistantMessages.AsNoTracking().Where(x => x.ApplicationUserId == userId && x.DraftId == draftId)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(80).ToListAsync(token);
        return rows.OrderBy(x => x.CreatedAt).ThenBy(x => x.Role == "user" ? 0 : 1).Select(ToResponse).ToArray();
    }

    public async Task<PersonalAssistantMessageResponse> SendAsync(Guid userId, SendPersonalAssistantMessageRequest request, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > 16000 ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160)
            throw new ArgumentException("Enter a bounded message and a stable request key.");
        var installation = await RequireInstallationAsync(userId, token);
        var gate = MessageGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"personal-assistant-send:" + userId.ToString("D")}, 0))", token);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.IdempotencyKey)));
            var existing = await db.PersonalAssistantMessages.SingleOrDefaultAsync(x => x.ApplicationUserId == userId && x.IdempotencyKey == key, token);
            if (existing is not null)
            {
                if (existing.Content != request.Content.Trim() || existing.DraftId != request.DraftId)
                    throw new InvalidOperationException("This request key already belongs to a different message.");
                return ToResponse(existing);
            }
            var draft = request.DraftId.HasValue ? await RequireDraftAsync(userId, request.DraftId.Value, token) : null;
            var history = (await ListMessagesAsync(userId, request.DraftId, token)).Where(x => x.Status == "Completed")
                .TakeLast(30).Select(x => new AssistantConversationMessage(x.Role, x.Content)).ToList();
            while (history.Sum(x => x.Content.Length) > 48000) history.RemoveAt(0);
            history.Add(new("user", request.Content.Trim()));
            var now = DateTimeOffset.UtcNow;
            var user = new PersonalAssistantMessage
            {
                Id = Guid.NewGuid(),
                ApplicationUserId = userId,
                DraftId = request.DraftId,
                Role = "user",
                Content = request.Content.Trim(),
                IdempotencyKey = key,
                CreatedAt = now
            };
            var answer = new PersonalAssistantMessage
            {
                Id = Guid.NewGuid(),
                ApplicationUserId = userId,
                DraftId = request.DraftId,
                Role = "assistant",
                Status = "Pending",
                IdempotencyKey = "answer:" + key,
                CreatedAt = now.AddTicks(1)
            };
            db.PersonalAssistantMessages.AddRange(user, answer);
            var work = await inbox.EnqueueAsync(PersonalAssistantCapabilities.UserScope(userId), installation, AgentWorkKind.Capability,
                PersonalAssistantCapabilities.Converse, JsonSerializer.SerializeToElement(new AssistantConversationRequest(history, draft?.ReviewJson), Json),
                $"personal-assistant-message:{user.Id:N}", now.AddMinutes(30), sourceType: "personal-assistant-message", sourceId: answer.Id.ToString("D"),
                cancellationToken: token);
            answer.AgentWorkItemId = work.Id;
            PersonalAssistantRealtime.Queue(db, userId, AppRealtimeEvents.PersonalAssistantChanged, answer.Id);
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            await scheduler.WakeAsync(installation, token);
            return ToResponse(user);
        }
        finally { gate.Release(); }
    }

    private async Task RequireUserAsync(Guid user, CancellationToken token)
    {
        if (user == Guid.Empty || !await db.Users.AnyAsync(x => x.Id == user, token))
            throw new UnauthorizedAccessException("A signed-in user is required.");
    }
    private async Task<RepositoryOnboardingDraft> RequireDraftAsync(Guid user, Guid id, CancellationToken token) =>
        await db.RepositoryOnboardingDrafts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.ApplicationUserId == user && x.Status != "Discarded", token)
        ?? throw new KeyNotFoundException("The onboarding draft was not found.");
    private static PersonalAssistantMessageResponse ToResponse(PersonalAssistantMessage x) => new(x.Id, x.Role, x.Content, x.Status, x.CreatedAt, x.DraftId);
}
