namespace CSweet.AgentRuntime.Artifacts;

public sealed class S3ArtifactStoreOptions
{
    public const string SectionName = "CSweet:AgentRuntime:Artifacts:S3";

    public string BucketName { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = "csweet-artifacts";
    public string Region { get; set; } = "us-east-1";
    public string? ServiceUrl { get; set; }
    public bool ForcePathStyle { get; set; } = true;
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    public long MaximumObjectBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BucketName) || BucketName.Length > 255 ||
            BucketName.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new InvalidOperationException("The S3 artifact bucket name is invalid.");
        if (string.IsNullOrWhiteSpace(KeyPrefix) || KeyPrefix.Length > 512 ||
            KeyPrefix.StartsWith('/') || KeyPrefix.Contains("..", StringComparison.Ordinal) ||
            KeyPrefix.Any(char.IsControl))
            throw new InvalidOperationException("The S3 artifact key prefix is invalid.");
        if (string.IsNullOrWhiteSpace(Region) || Region.Length > 100)
            throw new InvalidOperationException("The S3 artifact region is invalid.");
        if (ServiceUrl is not null)
        {
            if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp)
                throw new InvalidOperationException("The S3-compatible service URL is invalid.");
            if (endpoint.Scheme == Uri.UriSchemeHttp &&
                !string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                (!System.Net.IPAddress.TryParse(endpoint.Host, out var address) ||
                 !System.Net.IPAddress.IsLoopback(address)))
                throw new InvalidOperationException("Unencrypted S3-compatible endpoints are allowed only on loopback.");
        }
        if (MaximumObjectBytes is < 1 or > 100L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("The S3 artifact object limit is invalid.");
        if (string.IsNullOrWhiteSpace(AccessKeyId) != string.IsNullOrWhiteSpace(SecretAccessKey))
            throw new InvalidOperationException("Both S3 access-key fields must be supplied together.");
    }
}
