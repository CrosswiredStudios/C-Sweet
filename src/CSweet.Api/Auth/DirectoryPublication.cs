using CSweet.Domain.Setup;
using CSweet.HostedAccount;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.Api.Auth;

/// <summary>Wakes the directory publisher early, for example right after hosted setup completes.</summary>
public sealed class DirectoryPublicationSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try { if (_signal.CurrentCount == 0) _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await _signal.WaitAsync(timeout, ct);
}

/// <summary>The directory outbox over the Headquarters database (IL-12).</summary>
internal sealed class EfDirectoryPublicationStore(CSweetDbContext db, IDataProtectionProvider protection, TimeProvider time,
    IOptions<HostedAccountOptions> options) : IDirectoryPublicationStore
{
    private readonly IDataProtector _keyProtector = protection.CreateProtector(AuthenticationService.InstallationKeyProtectionPurpose);

    public async Task<LinkedInstallation?> GetActiveLinkAsync(CancellationToken ct)
    {
        var link = await ActiveLinkAsync(ct);
        // A link to another issuer (before relinking in Settings) publishes nothing to the configured one.
        if (link is null || !InstallationLinkEndpoints.SameIssuer(link.Issuer, options.Value.Issuer)) return null;
        var key = _keyProtector.Unprotect(Convert.FromBase64String(link.ProtectedInstallationKey));
        return new LinkedInstallation(link.InstallationId, link.Issuer, key);
    }

    public async Task EnsureOwnerPublicationAsync(LinkedInstallation linked, CancellationToken ct)
    {
        var link = await ActiveLinkAsync(ct);
        if (link is null) return;
        var subject = await db.UserLogins.AsNoTracking()
            .Where(x => x.UserId == link.LinkedByApplicationUserId && x.LoginProvider == link.Issuer)
            .Select(x => x.ProviderKey)
            .FirstOrDefaultAsync(ct);
        if (subject is null || await db.DirectoryPublications.AnyAsync(x => x.Subject == subject, ct)) return;

        var now = time.GetUtcNow();
        db.DirectoryPublications.Add(new DirectoryPublication
        {
            Id = Guid.NewGuid(),
            Subject = subject,
            ApplicationUserId = link.LinkedByApplicationUserId,
            Role = AuthenticationService.DirectoryOwnerRole,
            State = DirectoryPublicationState.Active,
            Revision = 1,
            NextAttemptAt = now,
            UpdatedAt = now
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear(); // added concurrently; the existing row wins
        }
    }

    public async Task<IReadOnlyList<PendingMemberPublication>> GetDueAsync(DateTimeOffset now, int max, CancellationToken ct)
    {
        var rows = await db.DirectoryPublications.AsNoTracking()
            .Where(x => x.Revision > x.PublishedRevision && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt)
            .Take(max)
            .ToListAsync(ct);
        return rows.Select(x => new PendingMemberPublication(x.Id, x.Subject, x.Role, x.State.ToString(), x.Revision, x.Attempts)).ToList();
    }

    public async Task MarkPublishedAsync(Guid id, long revision, CancellationToken ct)
    {
        var row = await db.DirectoryPublications.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return;
        row.PublishedRevision = Math.Max(row.PublishedRevision, revision);
        row.Attempts = 0;
        row.LastError = null;
        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task RaiseRevisionAsync(Guid id, long aboveRevision, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.DirectoryPublications.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return;
        row.Revision = Math.Max(row.Revision, aboveRevision + 1);
        row.NextAttemptAt = now;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkFailedAsync(Guid id, string error, DateTimeOffset nextAttemptAt, CancellationToken ct)
    {
        var row = await db.DirectoryPublications.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return;
        row.Attempts++;
        row.LastError = error.Length > 64 ? error[..64] : error;
        row.NextAttemptAt = nextAttemptAt;
        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private Task<InstallationLink?> ActiveLinkAsync(CancellationToken ct) =>
        db.InstallationLinks.AsNoTracking()
            .Where(x => x.State == InstallationLinkState.Active)
            .OrderByDescending(x => x.LinkedAt)
            .FirstOrDefaultAsync(ct);
}

/// <summary>Runs the directory publisher: at start (reconciliation), when signalled, and on a timer.</summary>
internal sealed class DirectoryPublicationService(
    IServiceScopeFactory scopes,
    DirectoryPublisher publisher,
    DirectoryPublicationSignal signal,
    IOptions<HostedAccountOptions> options,
    ILogger<DirectoryPublicationService> logger) : BackgroundService
{
    private static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.LinkingEnabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var attempted = 0;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    attempted = await publisher.RunOnceAsync(scope.ServiceProvider.GetRequiredService<IDirectoryPublicationStore>(), stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Publishing to the C-Sweet directory failed; will retry.");
                }
                await signal.WaitAsync(attempted > 0 ? BusyInterval : IdleInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
