namespace CSweet.ExecutionGateway;

public sealed class ExecutionGatewayOptions
{
    public const string SectionName = "CSweet:ExecutionGateway";
    public bool AllowInsecureDevelopmentLoopback { get; set; } = true;
    public string AssignmentSigningKeyId { get; set; } = "execution-gateway-ephemeral";
    public string AssignmentSigningPrivateKeyPkcs8Base64 { get; set; } = string.Empty;
    public string DevelopmentBootstrapKey { get; set; } = string.Empty;
}
