namespace CSweet.Contracts.Setup;

public sealed record RefreshLocalOfficeEnrollmentRequest(
    Guid AssistedSetupSessionId, string SetupReceipt, string MachineName,
    string OperatingSystem, string Architecture);
