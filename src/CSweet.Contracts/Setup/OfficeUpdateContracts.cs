namespace CSweet.Contracts.Setup;

public sealed record OfficeUpdateCheckResponse(
    string LatestVersion,
    DateTimeOffset CheckedAt,
    IReadOnlyList<OfficeUpdatePackageResponse> Packages, string Source = "github", string? Warning = null);

public sealed record OfficeUpdatePackageResponse(Guid OfficeId, string? Url, string? Version = null, string Source = "github");
