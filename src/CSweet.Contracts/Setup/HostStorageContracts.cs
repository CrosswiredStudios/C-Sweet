namespace CSweet.Contracts.Setup;

public sealed record ComputeStoragePreference(string? VolumeId);
public sealed record HostStorageLocation(string Name, string Path, long? Bytes, string Status, string Control);
public sealed record HostStorageReport(bool Windows, string? ComputeVolumeId, string? OfficeVolumeId,
    IReadOnlyList<LocalOfficeVolumeResponse> Volumes, IReadOnlyList<HostStorageLocation> Locations);
