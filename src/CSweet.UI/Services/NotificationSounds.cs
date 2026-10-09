using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;

namespace CSweet.UI.Services;

/// <summary>
/// Stable keys for UI events that can play a notification sound. Each key is mapped to a sound URL in
/// <see cref="NotificationSoundOptions.Sounds"/> (configuration section <c>CSweet:NotificationSounds:Sounds</c>).
/// </summary>
public static class NotificationSoundEvents
{
    /// <summary>Another participant posted a message that raised the signed-in user's Communications unread count.</summary>
    public const string CommunicationMessageReceived = "communications.message-received";

    /// <summary>
    /// Reserved for a new approval that needs the signed-in user's decision. Not raised yet; once it is, map it in
    /// configuration to give approvals their own sound.
    /// </summary>
    public const string ApprovalRequested = "approvals.requested";
}

/// <summary>Event-type to sound mapping and playback settings for in-app notification sounds.</summary>
public sealed class NotificationSoundOptions
{
    public const string ConfigurationSection = "CSweet:NotificationSounds";
    public const string DefaultMessageSound = "_content/CSweet.UI/sounds/notification.mp3";

    /// <summary>Master switch for all notification sounds.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Playback volume from 0 (silent) to 1 (full).</summary>
    public double Volume { get; set; } = 0.6;

    /// <summary>Minimum gap between two plays of the same event type, so a burst of messages chimes once.</summary>
    public int MinimumIntervalMilliseconds { get; set; } = 1500;

    /// <summary>
    /// Sound URL per <see cref="NotificationSoundEvents"/> key. URLs are relative to the app base or absolute.
    /// Configuration adds to or overrides these defaults; an empty value silences that event.
    /// </summary>
    public Dictionary<string, string> Sounds { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [NotificationSoundEvents.CommunicationMessageReceived] = DefaultMessageSound
    };

    public static NotificationSoundOptions FromConfiguration(IConfiguration? configuration)
    {
        var options = new NotificationSoundOptions();
        if (configuration is null) return options;
        var section = configuration.GetSection(ConfigurationSection);
        section.Bind(options);
        // Apply event mappings explicitly so an empty value reliably silences a default sound.
        var sounds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (eventType, url) in options.Sounds) sounds[eventType] = url;
        foreach (var sound in section.GetSection(nameof(Sounds)).GetChildren())
            sounds[sound.Key] = sound.Value ?? string.Empty;
        options.Sounds = sounds;
        return options;
    }

    public bool TryGetSound(string eventType, out string url)
    {
        url = string.Empty;
        if (!Enabled || string.IsNullOrWhiteSpace(eventType)) return false;
        string? configured = Sounds.TryGetValue(eventType, out var exact) ? exact :
            Sounds.FirstOrDefault(x => string.Equals(x.Key, eventType, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(configured)) return false;
        url = configured.Trim();
        return true;
    }
}

/// <summary>Plays the sound configured for a notification event type. Playback failures never surface to callers.</summary>
public sealed class NotificationSoundPlayer(IJSRuntime js, NotificationSoundOptions options) : IAsyncDisposable
{
    private const string ModulePath = "./_content/CSweet.UI/js/notificationSounds.js";
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _lastPlayedAt = new(StringComparer.OrdinalIgnoreCase);
    private Task<IJSObjectReference>? _module;

    /// <returns><c>true</c> when a sound was handed to the browser for playback.</returns>
    public async Task<bool> PlayAsync(string eventType, CancellationToken cancellationToken = default)
    {
        if (!options.TryGetSound(eventType, out var url)) return false;
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_lastPlayedAt.TryGetValue(eventType, out var last) &&
                now - last < Math.Max(0, options.MinimumIntervalMilliseconds)) return false;
            _lastPlayedAt[eventType] = now;
        }
        try
        {
            var module = await (_module ??= js.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath).AsTask());
            await module.InvokeVoidAsync("play", cancellationToken, url, Math.Clamp(options.Volume, 0d, 1d));
            return true;
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or
                                              InvalidOperationException or TaskCanceledException or OperationCanceledException)
        {
            if (_module is { IsCompletedSuccessfully: false, IsCompleted: true }) _module = null;
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not { IsCompletedSuccessfully: true } module) return;
        try { await module.Result.DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }
}
