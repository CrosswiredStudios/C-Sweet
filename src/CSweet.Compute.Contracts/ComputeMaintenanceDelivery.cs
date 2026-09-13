namespace CSweet.Compute.Contracts;

/// <summary>A fresh delivery envelope can carry historical offline evidence without changing its bytes or ID.</summary>
public sealed record ComputeMaintenanceDelivery(string EventJson, string EventDigest, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);
public sealed record SignedComputeMaintenanceDelivery(string KeyId, string PayloadJson, string SignatureBase64);
