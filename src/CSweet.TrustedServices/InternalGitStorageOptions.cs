namespace CSweet.TrustedServices;

public sealed class InternalGitStorageOptions
{
    public const string SectionName = "CSweet:SourceControl:Storage";
    public string RepositoryRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSweet", "SourceControl", "repositories");
    public string TemporaryRoot { get; set; } = Path.Combine(Path.GetTempPath(), "csweet-git");
    public string GitExecutable { get; set; } = "git";
    public InternalGitObjectStorageOptions Lfs { get; set; } = new();
    public InternalGitObjectStorageOptions Backup { get; set; } = new();
    public string? ExpectedStoreId { get; set; }
    public int OperationTimeoutSeconds { get; set; } = 120;
    public int MaximumOutputBytes { get; set; } = 4 * 1024 * 1024;

    public void Validate()
    {
        ValidatePath(RepositoryRoot);
        ValidatePath(TemporaryRoot);
        if (ExpectedStoreId is not null && (string.IsNullOrWhiteSpace(ExpectedStoreId) || ExpectedStoreId.Length > 128 || ExpectedStoreId.Any(char.IsControl)))
            throw new ArgumentException("Store identity must be a nonempty value of at most 128 characters.");
        if (OperationTimeoutSeconds is < 1 or > 3600 || MaximumOutputBytes is < 1024 or > 64 * 1024 * 1024)
            throw new ArgumentException("Invalid Git process limits.");
        Lfs.Validate();
        Backup.Validate();
    }

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            Path.GetFullPath(path) == Path.GetPathRoot(path))
            throw new ArgumentException("Storage must use an absolute directory path, not a filesystem root.");
    }
}

public sealed class InternalGitObjectStorageOptions
{
    public string Provider { get; set; } = "filesystem";
    public string? RootPath { get; set; }
    public string? ExpectedStoreId { get; set; }
    public long MaximumObjectBytes { get; set; } = 10L * 1024 * 1024 * 1024;
    public string? ServiceUrl { get; set; }
    public string? BucketName { get; set; }
    public string KeyPrefix { get; set; } = "csweet-source-control";
    public string Region { get; set; } = "us-east-1";
    public bool ForcePathStyle { get; set; } = true;
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }

    public void Validate()
    {
        if (Provider is not ("filesystem" or "s3")) throw new ArgumentException("Storage provider must be filesystem or s3.");
        if (RootPath is not null) InternalGitStorageOptions.ValidatePath(RootPath);
        if (MaximumObjectBytes < 1 || MaximumObjectBytes > 100L * 1024 * 1024 * 1024)
            throw new ArgumentException("Object size limit must be between one byte and 100 GiB.");
        if (Provider == "s3" && (string.IsNullOrWhiteSpace(BucketName) ||
            string.IsNullOrWhiteSpace(KeyPrefix) || KeyPrefix.Contains("..")))
            throw new ArgumentException("S3 storage requires a bucket and a valid key prefix.");
        if (ServiceUrl is not null && (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))))
            throw new ArgumentException("Object storage requires HTTPS, except on loopback.");
        if (string.IsNullOrWhiteSpace(AccessKeyId) != string.IsNullOrWhiteSpace(SecretAccessKey))
            throw new ArgumentException("Both S3 credential fields must be supplied together.");
    }

    public string Location(string fallback) => Provider == "s3" ? $"s3://{BucketName}/{KeyPrefix}" : RootPath ?? fallback;
}
