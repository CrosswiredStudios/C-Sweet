namespace CSweet.ExecutionGateway;

public sealed class ExecutionGatewayOptions
{
    public const string SectionName = "CSweet:ExecutionGateway";
    public string AssignmentSigningKeyId { get; set; } = string.Empty;
    public string AssignmentSigningPrivateKeyPkcs8Base64 { get; set; } = string.Empty;
    public string AssignmentSigningPrivateKeyPath { get; set; } = string.Empty;
}
