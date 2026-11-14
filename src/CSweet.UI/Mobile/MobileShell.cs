using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CSweet.UI.Mobile;

public enum EnterpriseProfileKind { Shared, Private }

/// <summary>
/// One enterprise (Headquarters) this phone can open. Shared profiles come from the C-Sweet directory; private
/// profiles (Phase 3) come from an invitation. <see cref="SecondLine"/> tells same-named enterprises apart.
/// </summary>
public sealed record EnterpriseProfile(
    string ProfileId,
    Guid HeadquartersId,
    string DisplayName,
    string? SecondLine,
    EnterpriseProfileKind Kind,
    string MemberLabel);

/// <summary>Profiles and the last selection, kept on the phone so launch can restore before the network answers.</summary>
public interface IEnterpriseProfileStore
{
    Task<IReadOnlyList<EnterpriseProfile>> ListAsync();
    Task ReplaceSharedAsync(IReadOnlyList<EnterpriseProfile> profiles);
    Task ClearSharedAsync();
    Task<string?> GetLastActiveAsync();
    Task SetLastActiveAsync(string? profileId);
}

public sealed class SecureEnterpriseProfileStore(ISecureValueStore store) : IEnterpriseProfileStore
{
    internal const string ProfilesKey = "csweet.profiles.v1";
    internal const string LastActiveKey = "csweet.profiles.last.v1";

    public async Task<IReadOnlyList<EnterpriseProfile>> ListAsync()
    {
        var json = await store.GetAsync(ProfilesKey);
        if (json is null) return [];
        try { return JsonSerializer.Deserialize<List<EnterpriseProfile>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public async Task ReplaceSharedAsync(IReadOnlyList<EnterpriseProfile> profiles)
    {
        var kept = (await ListAsync()).Where(x => x.Kind != EnterpriseProfileKind.Shared);
        await store.SetAsync(ProfilesKey, JsonSerializer.Serialize(kept.Concat(profiles).ToList()));
    }

    public async Task ClearSharedAsync()
    {
        var kept = (await ListAsync()).Where(x => x.Kind != EnterpriseProfileKind.Shared).ToList();
        if (kept.Count == 0) await store.RemoveAsync(ProfilesKey);
        else await store.SetAsync(ProfilesKey, JsonSerializer.Serialize(kept));
        var last = await GetLastActiveAsync();
        if (last is not null && kept.All(x => x.ProfileId != last)) await store.RemoveAsync(LastActiveKey);
    }

    public Task<string?> GetLastActiveAsync() => store.GetAsync(LastActiveKey);

    public Task SetLastActiveAsync(string? profileId) =>
        profileId is null ? store.RemoveAsync(LastActiveKey) : store.SetAsync(LastActiveKey, profileId);
}

/// <summary>Top-level phone screens. See the "Launch, sign in and choose an enterprise" design.</summary>
public enum MobileShellState
{
    /// <summary>Reading local profiles and the protected session.</summary>
    Restoring,
    /// <summary>Sign in.</summary>
    Entry,
    /// <summary>My Headquarters / Switch enterprise.</summary>
    Chooser,
    /// <summary>Enrolling this phone with the selected Headquarters (H4).</summary>
    Enrolling,
    /// <summary>Waiting for a trusted session to approve this phone (H4).</summary>
    PendingApproval,
    /// <summary>A Headquarters is selected; the phone is not connected to it yet.</summary>
    Connecting,
    /// <summary>Connected; the enterprise's own pages are shown (H4).</summary>
    Enterprise,
    Unreachable,
    Revoked
}

public enum MobileShellNotice { None, SignInCancelled, SignInFailed, SignInExpired, DirectoryUnavailable }

/// <summary>
/// Launch rules and account actions for the phone shell. No profiles → Sign in; one → open it; several on first
/// entry → chooser; a remembered last selection → open it. <see cref="Generation"/> increases on every activation
/// so results that finish for an older selection are ignored.
/// </summary>
public sealed class MobileSessionCoordinator(
    MobileAccountSession account,
    HeadquartersDirectoryClient directory,
    IEnterpriseProfileStore store,
    IBrowserAuthenticator browser,
    MobileDeviceKeyStore deviceKeys,
    IHeadquartersConnector connector)
{
    private bool _started;

    public MobileShellState State { get; private set; } = MobileShellState.Restoring;
    public IReadOnlyList<EnterpriseProfile> Profiles { get; private set; } = [];
    public EnterpriseProfile? ActiveProfile { get; private set; }
    public MobileShellNotice Notice { get; private set; }
    public bool Busy { get; private set; }
    public long Generation { get; private set; }
    public string? AccountEmail => account.Current?.Email;
    /// <summary>Result of the last Connect on the active enterprise; cleared when the selection changes.</summary>
    public HeadquartersConnectResult? LastConnection { get; private set; }
    public event Action? Changed;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started) return;
        _started = true;
        if (!await account.RestoreAsync())
        {
            Set(MobileShellState.Entry);
            return;
        }

        Profiles = await store.ListAsync();
        var last = await store.GetLastActiveAsync();
        var remembered = Profiles.FirstOrDefault(x => x.ProfileId == last);
        if (remembered is not null)
        {
            Generation++;
            ActiveProfile = remembered;
            Set(MobileShellState.Connecting);
            await LoadDirectoryAsync(autoSelect: false, ct);
        }
        else
        {
            await LoadDirectoryAsync(autoSelect: true, ct);
        }
    }

    public async Task SignInAsync(bool createAccount, CancellationToken ct = default)
    {
        if (Busy) return;
        Busy = true;
        Notice = MobileShellNotice.None;
        Changed?.Invoke();
        SignInOutcome outcome;
        try
        {
            outcome = await account.SignInAsync(browser, createAccount, ct);
        }
        finally
        {
            Busy = false;
        }

        if (outcome.Status != SignInStatus.Succeeded)
        {
            Notice = outcome.Status == SignInStatus.Cancelled ? MobileShellNotice.SignInCancelled : MobileShellNotice.SignInFailed;
            Set(MobileShellState.Entry);
            return;
        }

        // A different account may have signed in: never show the previous account's list.
        await store.ClearSharedAsync();
        Profiles = [];
        ActiveProfile = null;
        await LoadDirectoryAsync(autoSelect: true, ct);
    }

    public Task RefreshDirectoryAsync(CancellationToken ct = default) => LoadDirectoryAsync(autoSelect: false, ct);

    public async Task SelectAsync(string profileId)
    {
        var profile = Profiles.FirstOrDefault(x => x.ProfileId == profileId);
        if (profile is not null) await ActivateAsync(profile);
    }

    /// <summary>
    /// H4: gets a directory ticket for this phone's key, opens the relay channel and the pinned secure session to the
    /// active Headquarters, and records what Headquarters answered.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (ActiveProfile is not { } profile || Busy) return;
        var generation = Generation;
        Busy = true;
        LastConnection = null;
        Changed?.Invoke();
        HeadquartersConnectResult result;
        try
        {
            var token = await account.GetAccessTokenAsync(ct);
            var key = await deviceKeys.GetOrCreateAsync();
            var ticket = await directory.RequestTicketAsync(token, profile.HeadquartersId, MobileDeviceKeyStore.PublicKey(key), ct);
            result = await connector.ConnectAsync(ticket, key, ct);
        }
        catch (AccountSessionExpiredException)
        {
            Busy = false;
            await SignOutCoreAsync();
            Notice = MobileShellNotice.SignInExpired;
            Set(MobileShellState.Entry);
            return;
        }
        catch (DirectoryTicketException error)
        {
            result = new HeadquartersConnectResult(error.Code == "relay_not_configured" ? HeadquartersConnectStatus.NotConfigured : HeadquartersConnectStatus.NoAccess);
        }
        catch (Exception error) when (!ct.IsCancellationRequested && AccountSignInClient.IsTransient(error))
        {
            result = new HeadquartersConnectResult(HeadquartersConnectStatus.Unreachable);
        }
        finally
        {
            Busy = false;
        }

        if (generation != Generation) return; // another enterprise was selected meanwhile
        LastConnection = result;
        Changed?.Invoke();
    }

    public void OpenChooser() => Set(MobileShellState.Chooser);

    /// <summary>Closes the chooser back to the current enterprise, if there is one.</summary>
    public void CloseChooser()
    {
        if (ActiveProfile is not null) Set(MobileShellState.Connecting);
    }

    public async Task SignOutAsync()
    {
        await SignOutCoreAsync();
        Notice = MobileShellNotice.None;
        Set(MobileShellState.Entry);
    }

    private async Task LoadDirectoryAsync(bool autoSelect, CancellationToken ct)
    {
        var generation = Generation;
        Busy = true;
        Changed?.Invoke();
        IReadOnlyList<DirectoryEntry> entries;
        try
        {
            var token = await account.GetAccessTokenAsync(ct);
            entries = await directory.ListAsync(token, ct);
        }
        catch (AccountSessionExpiredException)
        {
            Busy = false;
            await SignOutCoreAsync();
            Notice = MobileShellNotice.SignInExpired;
            Set(MobileShellState.Entry);
            return;
        }
        catch (Exception exception) when (!ct.IsCancellationRequested && AccountSignInClient.IsTransient(exception))
        {
            Busy = false;
            Notice = MobileShellNotice.DirectoryUnavailable;
            Set(ActiveProfile is not null && State == MobileShellState.Connecting ? MobileShellState.Connecting : MobileShellState.Chooser);
            return;
        }

        Busy = false;
        Notice = MobileShellNotice.None;
        Profiles = ToProfiles(entries);
        await store.ReplaceSharedAsync(Profiles);

        if (ActiveProfile is not null)
        {
            var current = Profiles.FirstOrDefault(x => x.ProfileId == ActiveProfile.ProfileId);
            if (current is null)
            {
                // Access was removed: forget the selection and let the person choose again.
                ActiveProfile = null;
                await store.SetLastActiveAsync(null);
                Set(MobileShellState.Chooser);
            }
            else
            {
                ActiveProfile = current;
                Changed?.Invoke();
            }
            return;
        }

        if (autoSelect && generation == Generation && Profiles.Count == 1)
            await ActivateAsync(Profiles[0]);
        else
            Set(MobileShellState.Chooser);
    }

    private async Task ActivateAsync(EnterpriseProfile profile)
    {
        Generation++;
        ActiveProfile = profile;
        LastConnection = null;
        await store.SetLastActiveAsync(profile.ProfileId);
        Set(MobileShellState.Connecting);
    }

    private async Task SignOutCoreAsync()
    {
        Generation++;
        await account.SignOutAsync();
        await store.ClearSharedAsync();
        Profiles = [];
        ActiveProfile = null;
        LastConnection = null;
    }

    private void Set(MobileShellState state)
    {
        State = state;
        Changed?.Invoke();
    }

    internal static IReadOnlyList<EnterpriseProfile> ToProfiles(IReadOnlyList<DirectoryEntry> entries)
    {
        var duplicates = entries.GroupBy(x => x.DisplayName.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        return entries
            .Select(x => new EnterpriseProfile(
                $"shared:{x.InstallationId:N}",
                x.InstallationId,
                x.DisplayName,
                duplicates.Contains(x.DisplayName.Trim()) ? x.InstallationId.ToString("N")[..8] : null,
                EnterpriseProfileKind.Shared,
                x.Role))
            .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.HeadquartersId)
            .ToList();
    }
}

public static class MobileShellServiceCollectionExtensions
{
    /// <summary>
    /// Registers the phone shell. The host supplies <see cref="IBrowserAuthenticator"/> and <see cref="ISecureValueStore"/>.
    /// </summary>
    public static IServiceCollection AddCSweetMobileShell(this IServiceCollection services, MobileAccountOptions options)
    {
        options.Validate();
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        services.AddSingleton(sp => new AccountSignInClient(http, options, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(_ => new HeadquartersDirectoryClient(http, options));
        services.AddSingleton<MobileAccountSession>();
        services.AddSingleton<IEnterpriseProfileStore, SecureEnterpriseProfileStore>();
        services.AddSingleton<MobileDeviceKeyStore>();
        services.AddSingleton<MobileSessionCoordinator>();
        return services;
    }
}
