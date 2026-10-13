namespace CSweet.Contracts.Agents;

public sealed record ReviewRuntimeInternetRequest(bool Approved, Guid ExpectedPackageVersionId, long ExpectedGrantRevision);
