namespace CSweet.Contracts.Mobile;

/// <summary>One of the signed-in person's phones, as listed on Settings → Phones.</summary>
public sealed record MobileDeviceResponse(
    Guid Id,
    string Label,
    string Fingerprint,
    string State,
    DateTimeOffset RequestedAt,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? LastSeenAt);
