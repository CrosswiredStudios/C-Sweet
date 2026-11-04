namespace CSweet.Contracts.Plugins;

public sealed record PluginAccountUpgradeReview(Guid SourceInstallationId,Guid TargetInstallationId,string SourceVersion,
    string TargetVersion,string SourcePackageDigest,string TargetPackageDigest,string ServiceOrigin,
    int ResourceCount,int BusinessGrantCount,IReadOnlyList<string> GrantedScopeSets,DateTimeOffset ExpiresAt,string ReviewToken)
{public override string ToString()=>"Compatible account upgrade review [review token redacted]";}
public sealed record ApprovePluginAccountUpgrade(string ReviewToken)
{public override string ToString()=>"Compatible account upgrade approval [review token redacted]";}
public sealed record PluginAccountUpgradeProgress(Guid Id,Guid SourceInstallationId,Guid TargetInstallationId,string Status);
public sealed record PluginAccountUpgradeStatus(PluginAccountUpgradeProgress? Upgrade);
