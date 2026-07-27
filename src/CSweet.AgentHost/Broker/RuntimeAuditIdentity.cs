using CSweet.Application.Setup;

namespace CSweet.AgentHost.Broker;

internal static class RuntimeAuditIdentity
{
    public static Guid? OrganizationId(AgentSession session) =>
        Guid.TryParse(session.BusinessId, out var id) ? id : null;

    public static AuditActor Actor(AgentSession session, string? remotePeer = null) => new(
        "Agent", true, DisplayName: session.AgentId, AgentId: session.AgentId,
        InstallationId: Parse(session.InstallationId), RuntimeInstanceId: Parse(session.RuntimeInstanceId),
        TickId: Parse(session.TickId), SessionId: session.SessionId, PackageId: session.AgentId,
        PackageVersion: session.AgentVersion, RemotePeer: remotePeer);

    public static AuditTarget Target(AgentSession session) => new(
        "Agent", session.AgentId, session.AgentId, Parse(session.InstallationId), session.SessionId);

    private static Guid? Parse(string? value) => Guid.TryParse(value, out var id) ? id : null;
}
