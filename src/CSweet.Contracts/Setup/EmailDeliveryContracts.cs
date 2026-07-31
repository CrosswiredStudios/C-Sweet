using System.ComponentModel.DataAnnotations;

namespace CSweet.Contracts.Setup;

public static class EmailDeliveryProviderKeys
{
    public const string CustomSmtp = "custom-smtp";
    public const string Gmail = "gmail";
    public const string Microsoft365 = "microsoft-365";
    public const string SendGrid = "sendgrid";
    public const string AmazonSes = "amazon-ses";
}

public sealed record EmailDeliveryProfileResponse(
    Guid Id,
    string Name,
    string ProviderKey,
    string Host,
    int Port,
    bool EnableSsl,
    string? UserName,
    bool HasPassword,
    string FromAddress,
    string FromName,
    string PublicAppUrl,
    bool IsDefault,
    bool IsConfigured,
    bool IsReady,
    DateTimeOffset ConfiguredAt,
    DateTimeOffset? LastTestSucceededAt,
    DateTimeOffset UpdatedAt);

public sealed record SaveEmailDeliveryProfileRequest(
    [property: Required, MaxLength(160)] string Name,
    [property: Required, MaxLength(64)] string ProviderKey,
    [property: Required, MaxLength(253)] string Host,
    [property: Range(1, 65535)] int Port,
    bool EnableSsl,
    [property: MaxLength(320)] string? UserName,
    string? Password,
    bool ClearPassword,
    [property: Required, EmailAddress, MaxLength(320)] string FromAddress,
    [property: Required, MaxLength(160)] string FromName,
    [property: Required, Url, MaxLength(2048)] string PublicAppUrl);

public sealed record EmailDeliveryProfileActionResponse(
    bool Succeeded,
    string? ErrorCode,
    string Message,
    EmailDeliveryProfileResponse? Profile = null,
    IReadOnlyList<EmailDeliveryProfileResponse>? Profiles = null);
