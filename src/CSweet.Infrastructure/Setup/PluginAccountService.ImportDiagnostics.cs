using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class PluginAccountService
{
    private static PluginAccountImportProgress? ImportProgress(PluginAccountSetupSession session)
    {
        if (session.ImportDiagnosticsJson is not { Length: > 0 and <= 2048 } json) return null;
        try
        {
            var value = JsonSerializer.Deserialize<PluginAccountImportProgress>(json, Json);
            return value?.IsSafe() == true ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static void WriteImportProgress(PluginAccountSetupSession session, PluginAccountImportProgress progress)
    {
        if (!progress.IsSafe()) throw new InvalidOperationException("Invalid import diagnostic classification.");
        session.ImportDiagnosticsJson = JsonSerializer.Serialize(progress, Json);
    }

    private static void StartImportProgress(PluginAccountSetupSession session)
    {
        var prior = ImportProgress(session);
        var now = DateTimeOffset.UtcNow;
        WriteImportProgress(session, new(prior?.CorrelationId ?? Guid.NewGuid(), "Running", "Authority",
            Math.Min((prior?.Attempt ?? 0) + 1, 1_000_000), prior?.QueuedAt, now));
    }

    private static void ImportStage(PluginAccountSetupSession session, string stage)
    {
        if (ImportProgress(session) is { } progress) WriteImportProgress(session, progress with { Stage = stage });
    }

    private async Task SaveImportStage(PluginAccountSetupSession session, string stage, CancellationToken ct)
    {
        ImportStage(session, stage); session.Revision++;
        await db.SaveChangesAsync(ct);
    }

    private static void FinishImportProgress(PluginAccountSetupSession session, string state, Exception? error = null,
        PluginAccountImportProgress? progress = null)
    {
        progress ??= ImportProgress(session);
        if (progress is null) return;
        var type = error switch
        {
            null => null, HttpRequestException => "HttpRequestException", JsonException => "JsonException",
            UnauthorizedAccessException => "UnauthorizedAccessException", OperationCanceledException => "OperationCanceledException",
            InvalidOperationException => "InvalidOperationException", _ => "Other"
        };
        var kind = error switch
        {
            null => null, HttpRequestException => "http_error", ProviderImportException => "http_error",
            UnauthorizedAccessException => "authority_changed", OperationCanceledException => "interrupted",
            JsonException or InvalidOperationException => "invalid_response", _ => "unexpected"
        };
        int? status = error is HttpRequestException http ? (int?)http.StatusCode : error is ProviderImportException providerError ? providerError.Status : null;
        if (status is < 100 or > 599) status = null;
        WriteImportProgress(session, progress with
        {
            State = state, FinishedAt = DateTimeOffset.UtcNow,
            Stage = state == "Completed" ? "Complete" : progress.Stage,
            FailureKind = kind, ExceptionType = type, HttpStatus = status,
            HttpStatusSource = status is null ? null : error is ProviderImportException ? "Provider" : progress.Stage == "ManagedDiscovery" ? "ManagedService" : progress.Stage is "IdentityRead" or "ResourceRead" ? "Provider" : "Upstream"
        });
    }

    // Preserve the existing interrupted-lease recovery behavior. Only diagnostics are saved here;
    // never flush partially tracked inventory/authority changes or overwrite another lease owner.
    private async Task RecordInterruptedImport(Guid id, Guid owner, PluginAccountImportProgress? progress, Exception error)
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            db.ChangeTracker.Clear();
            var session = await db.PluginAccountSetupSessions.SingleAsync(x => x.Id == id, budget.Token);
            if (session.Status != "Importing" || session.LeaseOwner != owner) return;
            FinishImportProgress(session, "Interrupted", error, progress);
            session.Revision++;
            await db.SaveChangesAsync(budget.Token);
        }
        catch (Exception)
        { /* Best-effort diagnostics cannot hide or replace the original exception, which the caller rethrows. */ }
    }

    private sealed class ProviderImportException(int status) : InvalidOperationException("Provider account discovery failed.")
    { public int Status { get; } = status; }
}
