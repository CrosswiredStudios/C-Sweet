namespace CSweet.Contracts.Setup;

public sealed record OfficeComputeSetupRequest(Guid AssistedSetupSessionId, string SetupReceipt, string MachineName,
    string OperatingSystem, string Architecture, string OfficeVersion, string ImageDigest);

public sealed record OfficeComputeSetupPlan(bool Enabled, string? Reason, Guid? OrganizationId = null,
    int MaximumEnvironments = 0, int CpuCount = 0, int MemoryMiB = 0, int DiskMiB = 0);
