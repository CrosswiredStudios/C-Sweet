namespace CSweet.Contracts.Plugins;

public sealed record PluginAccountBindingRequest(Guid RequesterInstallationId, string DependencyId, Guid ResourceId,
    Guid ConnectionId, long BusinessGrantRevision, string PackageDigest);
