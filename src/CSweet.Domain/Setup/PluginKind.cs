namespace CSweet.Domain.Setup;

/// <summary>Describes the platform role of an imported workload.</summary>
public enum PluginKind
{
    Agent,
    Service,
    Connector
}

/// <summary>Controls whether a plugin is bound to one organization or to the C-Sweet installation.</summary>
public enum PluginInstallationScope
{
    Organization,
    System
}
