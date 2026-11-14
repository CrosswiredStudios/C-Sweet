using Microsoft.Extensions.Options;

namespace CSweet.HostedAccount;

/// <summary>The active C-Sweet link of this Headquarters, with its installation key unprotected for signing.</summary>
public sealed record LinkedInstallation(Guid InstallationId, string Issuer, byte[] PrivateKey);

/// <summary>One person's desired directory entry that C-Sweet has not confirmed yet.</summary>
public sealed record PendingMemberPublication(Guid Id, string Subject, string Role, string State, long Revision, int Attempts);

/// <summary>Headquarters' side of the directory outbox (IL-12). Implemented over the Headquarters database.</summary>
public interface IDirectoryPublicationStore
{
    Task<LinkedInstallation?> GetActiveLinkAsync(CancellationToken ct);

    /// <summary>Bounded reconciliation: make sure the linking owner has an entry (installs that finished setup before
    /// publication existed, or lost it). Adds nothing when one exists.</summary>
    Task EnsureOwnerPublicationAsync(LinkedInstallation link, CancellationToken ct);

    /// <summary>Entries whose revision is ahead of what C-Sweet confirmed and whose next attempt is due, oldest first.</summary>
    Task<IReadOnlyList<PendingMemberPublication>> GetDueAsync(DateTimeOffset now, int max, CancellationToken ct);

    /// <summary>Record that C-Sweet holds <paramref name="revision"/>. Never lowers the confirmed revision.</summary>
    Task MarkPublishedAsync(Guid id, long revision, CancellationToken ct);

    /// <summary>C-Sweet holds a newer revision than ours (for example after a database restore). Headquarters is the
    /// source of truth, so move our revision above it and publish again right away.</summary>
    Task RaiseRevisionAsync(Guid id, long aboveRevision, DateTimeOffset now, CancellationToken ct);

    Task MarkFailedAsync(Guid id, string error, DateTimeOffset nextAttemptAt, CancellationToken ct);
}

/// <summary>
/// Publishes due directory entries for a linked Headquarters. Private installations (no active link) publish nothing.
/// Safe to run repeatedly and concurrently with new entries: C-Sweet keeps only the highest revision per person.
/// </summary>
public sealed class DirectoryPublisher(HostedAccountClient client, IOptions<HostedAccountOptions> options, TimeProvider time)
{
    public const int BatchSize = 20;
    public static readonly TimeSpan PermanentFailureRetry = TimeSpan.FromHours(6);

    /// <summary>Returns how many entries were attempted.</summary>
    public async Task<int> RunOnceAsync(IDirectoryPublicationStore store, CancellationToken ct = default)
    {
        if (!options.Value.LinkingEnabled) return 0;
        var link = await store.GetActiveLinkAsync(ct);
        if (link is null) return 0;
        // Never send this installation's directory to an issuer it was not linked with.
        if (!string.Equals(link.Issuer, options.Value.IssuerUri.AbsoluteUri, StringComparison.Ordinal)) return 0;

        await store.EnsureOwnerPublicationAsync(link, ct);
        var due = await store.GetDueAsync(time.GetUtcNow(), BatchSize, ct);
        if (due.Count == 0) return 0;

        using var key = InstallationKey.Import(link.PrivateKey);
        foreach (var item in due)
        {
            var now = time.GetUtcNow();
            try
            {
                var result = await client.PublishMemberAsync(link.InstallationId, key, item.Subject, item.Role, item.State, item.Revision, ct);
                switch (result.Status)
                {
                    case MemberPublicationStatus.Applied:
                        await store.MarkPublishedAsync(item.Id, item.Revision, ct);
                        break;
                    case MemberPublicationStatus.Stale when result.MarketRevision > item.Revision:
                        await store.RaiseRevisionAsync(item.Id, result.MarketRevision, now, ct);
                        break;
                    case MemberPublicationStatus.Stale:
                        await store.MarkPublishedAsync(item.Id, item.Revision, ct);
                        break;
                    case MemberPublicationStatus.InstallationRetired:
                        await store.MarkFailedAsync(item.Id, "installation_retired", now + PermanentFailureRetry, ct);
                        break;
                    case MemberPublicationStatus.UnknownAccount:
                        await store.MarkFailedAsync(item.Id, "unknown_account", now + PermanentFailureRetry, ct);
                        break;
                }
            }
            catch (Exception exception) when (!ct.IsCancellationRequested &&
                exception is HostedAccountException or HttpRequestException or TaskCanceledException)
            {
                var code = exception is HostedAccountException hosted ? hosted.Code : "unreachable";
                await store.MarkFailedAsync(item.Id, code, now + Backoff(item.Attempts), ct);
            }
        }
        return due.Count;
    }

    /// <summary>30 seconds, doubling, at most 15 minutes.</summary>
    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(900, 30 * Math.Pow(2, Math.Clamp(attempts, 0, 10))));
}
