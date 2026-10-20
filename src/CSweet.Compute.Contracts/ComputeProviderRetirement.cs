namespace CSweet.Compute.Contracts;

// HTTPS node-authenticated discovery delivers this one-purpose cleanup receipt capability.
// It is never an execution grant and cannot authorize any other mutation.
public sealed record ComputeProviderRetirement(Guid CleanupId, Guid BusinessId, Guid NodeId, string ReceiptToken);
public sealed record ComputeProviderRetirementReceipt(Guid CleanupId, string ReceiptToken);
