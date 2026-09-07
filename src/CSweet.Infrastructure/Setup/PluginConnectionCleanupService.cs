using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Durable host-only credential cleanup. No token value is stored in operational state.</summary>
public sealed class PluginConnectionCleanupService(CSweetDbContext db, IPluginSecretStore secrets,
    IHttpClientFactory http, IPluginProviderProfileRegistry profiles, IAuditEventWriter audit)
{
    public const string PendingKind = "host-connection-cleanup-pending";
    public const string CompletedKind = "host-connection-cleanup-completed";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public sealed record Cleanup(Guid ConnectionId, string DeclarationId, string ProfileId,
        string? AuthorizationEndpoint, string? TokenEndpoint, string? RevocationEndpoint,
        IReadOnlyList<string> SecretKeys, DateTimeOffset Deadline, DateTimeOffset NextAttempt,
        int Attempts = 0, bool RevocationConfirmed = false, string? Outcome = null, string? ActiveCredentialKey = null);

    public async Task ProcessPendingAsync(CancellationToken ct)
    {
        var jobs = await db.PluginOperationalStates.Where(x => x.Kind == PendingKind)
            .OrderBy(x => x.UpdatedAt).Take(25).ToListAsync(ct);
        foreach (var job in jobs) await ProcessAsync(job, ct);
    }

    public async Task ProcessAsync(PluginOperationalState job, CancellationToken ct)
    {
        if (job.Kind != PendingKind) return;
        var state = JsonSerializer.Deserialize<Cleanup>(job.PayloadJson, Json)
            ?? throw new InvalidOperationException("The credential cleanup record is invalid.");
        var now = DateTimeOffset.UtcNow;
        if (state.NextAttempt > now) return;
        // An optimistic durable claim also spaces retries across host instances.
        state = state with { Attempts = state.Attempts + 1, NextAttempt = now.AddMinutes(2) };
        Save(job, state);
        await db.SaveChangesAsync(ct);
        var connection = await db.PluginConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == state.ConnectionId &&
            x.AgentInstallationId == job.AgentInstallationId, ct);
        if (connection is not null && connection.Status != PluginConnectionStatus.Revoked)
            throw new InvalidOperationException("Credential cleanup cannot operate on a connected account.");

        var activeKey = state.ActiveCredentialKey ?? PluginOAuthCredentialKeys.Legacy(state.ConnectionId);
        var quarantineKey = $"oauth.cleanup.{job.Id:N}.token";
        // Reconnect is blocked while this job exists. Copy before removal so a crash never
        // loses the only revocation credential. The quarantined value is never broker-readable.
        var tokenJson = await secrets.GetAsync(job.AgentInstallationId, quarantineKey, ct);
        if (tokenJson is null)
        {
            tokenJson = await secrets.GetAsync(job.AgentInstallationId, activeKey, ct);
            if (tokenJson is not null) await secrets.SetAsync(job.AgentInstallationId, quarantineKey, tokenJson, ct);
        }
        await secrets.RemoveAsync(job.AgentInstallationId, activeKey, ct);
        await secrets.RemoveAsync(job.AgentInstallationId, PluginOAuthCredentialKeys.Legacy(state.ConnectionId), ct);
        foreach (var key in state.SecretKeys) await secrets.RemoveAsync(job.AgentInstallationId, key, ct);

        var confirmed = state.RevocationConfirmed;
        var outcome = tokenJson is null ? "NoCredentialMaterial" : "RevocationPending";
        if (tokenJson is not null && !confirmed && now < state.Deadline)
        {
            var profile = await profiles.ResolveAsync(state.ProfileId, ct);
            // Never send an old credential to a subsequently reconfigured destination.
            if (profile is not null && profile.AuthorizationEndpoint == state.AuthorizationEndpoint &&
                profile.TokenEndpoint == state.TokenEndpoint && profile.RevocationEndpoint == state.RevocationEndpoint &&
                Uri.TryCreate(state.RevocationEndpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme == "https" &&
                string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Fragment))
            {
                using var token = JsonDocument.Parse(tokenJson);
                var value = token.RootElement.TryGetProperty("refreshToken", out var refresh) && refresh.ValueKind == JsonValueKind.String
                    ? refresh.GetString() : token.RootElement.TryGetProperty("accessToken", out var access) ? access.GetString() : null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    try
                    {
                        using var response = await http.CreateClient(nameof(PluginSetupService)).PostAsync(endpoint,
                            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = value }), ct);
                        confirmed = response.IsSuccessStatusCode;
                    }
                    catch (HttpRequestException) { /* A later durable attempt retries; no response/error content is retained. */ }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }
            }
        }
        var complete = tokenJson is null || confirmed || now >= state.Deadline;
        if (complete)
        {
            await secrets.RemoveAsync(job.AgentInstallationId, quarantineKey, ct);
            outcome = confirmed ? "RevocationConfirmed" : tokenJson is null ? "NoCredentialMaterial" : "RevocationUnconfirmed";
            job.Kind = CompletedKind;
            job.ExternalKey = job.Id.ToString("N");
        }
        Save(job, state with { RevocationConfirmed = confirmed, Outcome = outcome });
        await db.SaveChangesAsync(ct);
        if (complete)
            await audit.WriteAsync("plugin-connection.cleanup.completed", nameof(PluginConnection), state.ConnectionId,
                confirmed ? "Provider revocation confirmed; local credential material removed."
                    : "Local credential cleanup completed; provider revocation was not confirmed.", cancellationToken: ct);
    }

    public static void Save(PluginOperationalState job, Cleanup state)
    {
        job.PayloadJson = JsonSerializer.Serialize(state, Json);
        job.Revision++;
        job.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
