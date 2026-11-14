using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CSweet.HostedAccount;

public enum HostedSetupState { Waiting, Approved, Denied, Expired, Failed }

public sealed record HostedSetupSnapshot(
    string AttemptId,
    HostedSetupState State,
    string UserCode,
    Uri VerificationUriComplete,
    Uri CreateAccountUri,
    DateTimeOffset ExpiresAt,
    TimeSpan PollInterval,
    string InstallationName,
    string? Email,
    string? Error);

/// <summary>What first-run setup needs to create the administrator and record the link.</summary>
public sealed record HostedSetupApproval(HostedIdentity Identity, Guid InstallationId, string InstallationName, byte[] PrivateKey);

/// <summary>
/// Hosted first-run linking attempts, kept in memory for the life of the device code (about 15 minutes).
/// A restart abandons an attempt and the person starts again. The attempt id is a random secret that only the
/// setup browser holds (in an HttpOnly cookie), so another browser cannot poll or complete it.
/// After approval the installation is registered with the issuer straight away, while the short-lived access
/// token is valid; registration is idempotent by key, so an abandoned attempt leaves at most an unused record.
/// </summary>
public sealed class HostedSetupCoordinator(HostedAccountClient client, TimeProvider time) : IDisposable
{
    public const int MaximumAttempts = 20;
    private readonly ConcurrentDictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);

    public async Task<HostedSetupSnapshot> StartAsync(string installationName, CancellationToken ct = default)
    {
        var name = installationName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 80 || name.Any(char.IsControl))
            throw new HostedAccountException("invalid_installation_name", "Enter a name of up to 80 characters for this Headquarters.");
        Prune();
        if (_attempts.Count >= MaximumAttempts)
            throw new HostedAccountException("too_many_attempts", "Too many sign-in attempts are in progress. Try again in a few minutes.");

        var key = InstallationKey.Create();
        try
        {
            var device = await client.StartAsync(name, key, ct);
            var id = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
            var attempt = new Attempt(device, key, name) { NextPollAt = time.GetUtcNow() + device.PollInterval, Interval = device.PollInterval };
            _attempts[id] = attempt;
            return Snapshot(id, attempt);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public async Task<HostedSetupSnapshot?> PollAsync(string attemptId, CancellationToken ct = default)
    {
        if (!_attempts.TryGetValue(attemptId, out var attempt)) return null;
        await attempt.Lock.WaitAsync(ct);
        try
        {
            if (attempt.State != HostedSetupState.Waiting) return Snapshot(attemptId, attempt);
            var now = time.GetUtcNow();
            if (now >= attempt.Device.ExpiresAt)
            {
                attempt.State = HostedSetupState.Expired;
                return Snapshot(attemptId, attempt);
            }
            if (now < attempt.NextPollAt) return Snapshot(attemptId, attempt);

            DevicePollResult result;
            try { result = await client.PollAsync(attempt.Device.DeviceCode, ct); }
            catch (HostedAccountException exception) { result = new DevicePollResult(DevicePollStatus.Failed, Error: exception.Message); }
            catch (HttpRequestException) { result = new DevicePollResult(DevicePollStatus.Pending); } // transient: try again next interval

            switch (result.Status)
            {
                case DevicePollStatus.SlowDown:
                    attempt.Interval += TimeSpan.FromSeconds(5);
                    break;
                case DevicePollStatus.Denied:
                    attempt.State = HostedSetupState.Denied;
                    break;
                case DevicePollStatus.Expired:
                    attempt.State = HostedSetupState.Expired;
                    break;
                case DevicePollStatus.Failed:
                    attempt.State = HostedSetupState.Failed;
                    attempt.Error = result.Error;
                    break;
                case DevicePollStatus.Approved:
                    attempt.Identity = result.Identity;
                    try
                    {
                        var registration = await client.RegisterInstallationAsync(result.AccessToken!, attempt.Key, ct);
                        attempt.InstallationId = registration.InstallationId;
                        attempt.State = HostedSetupState.Approved;
                    }
                    catch (Exception exception) when (exception is HostedAccountException or HttpRequestException)
                    {
                        attempt.State = HostedSetupState.Failed;
                        attempt.Error = exception is HostedAccountException hosted ? hosted.Message : "C-Sweet could not be reached to register this Headquarters.";
                    }
                    break;
            }
            attempt.NextPollAt = time.GetUtcNow() + attempt.Interval;
            return Snapshot(attemptId, attempt);
        }
        finally { attempt.Lock.Release(); }
    }

    public HostedSetupApproval? GetApproval(string attemptId) =>
        _attempts.TryGetValue(attemptId, out var attempt) && attempt.State == HostedSetupState.Approved && attempt.Identity is { } identity
            ? new HostedSetupApproval(identity, attempt.InstallationId, attempt.Name, attempt.Key.ExportPrivateKey())
            : null;

    public void Remove(string attemptId)
    {
        if (_attempts.TryRemove(attemptId, out var attempt)) attempt.Dispose();
    }

    public void Dispose()
    {
        foreach (var id in _attempts.Keys) Remove(id);
    }

    private void Prune()
    {
        var cutoff = time.GetUtcNow() - TimeSpan.FromMinutes(30);
        foreach (var (id, attempt) in _attempts)
            if (attempt.Device.ExpiresAt < cutoff) Remove(id);
    }

    private static HostedSetupSnapshot Snapshot(string id, Attempt attempt) =>
        new(id, attempt.State, attempt.Device.UserCode, attempt.Device.VerificationUriComplete, attempt.Device.CreateAccountUri,
            attempt.Device.ExpiresAt, attempt.Interval, attempt.Name, attempt.Identity?.Email, attempt.Error);

    private sealed class Attempt(DeviceAuthorization device, InstallationKey key, string name) : IDisposable
    {
        public DeviceAuthorization Device { get; } = device;
        public InstallationKey Key { get; } = key;
        public string Name { get; } = name;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public HostedSetupState State { get; set; }
        public DateTimeOffset NextPollAt { get; set; }
        public TimeSpan Interval { get; set; }
        public HostedIdentity? Identity { get; set; }
        public Guid InstallationId { get; set; }
        public string? Error { get; set; }

        public void Dispose()
        {
            Key.Dispose();
            Lock.Dispose();
        }
    }
}
