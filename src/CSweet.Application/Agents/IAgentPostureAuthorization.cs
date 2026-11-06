namespace CSweet.Application.Agents;

/// <summary>
/// The authorization seam for posture changes.
/// <para>
/// The posture is a human control surface that can grant an agent authority to act without a prompt,
/// so the write must not be reachable by resolving the service alone. Production binds this to the
/// owner/manager rule; a request that carries no resolvable human is refused.
/// </para>
/// </summary>
public interface IAgentPostureAuthorization
{
    /// <summary>
    /// True when this human may change the posture of this installation. Implementations must return
    /// false rather than throw for unknown installations, so the caller cannot distinguish one it may
    /// not manage from one that does not exist.
    /// </summary>
    ValueTask<bool> CanChangeAsync(
        Guid installationId,
        Guid applicationUserId,
        bool administrator,
        CancellationToken cancellationToken = default);
}
