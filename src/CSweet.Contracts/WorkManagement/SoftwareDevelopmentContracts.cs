using System.ComponentModel.DataAnnotations;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Contracts.WorkManagement;

public sealed record CreateGitRepositoryConnectionRequest(
    [property: Required, MaxLength(160)] string Name,
    string Provider,
    [property: Required, MaxLength(2048)] string CloneUrl,
    [property: Required, MaxLength(512)] string PermittedRepositoryPath,
    string AuthenticationMode,
    bool AllowPush,
    [property: Required, MaxLength(255)] string DefaultBranch,
    string PullRequestProvider,
    IReadOnlyList<string> AllowedHosts,
    IReadOnlyList<int> AllowedPorts,
    IReadOnlyList<string>? SshHostFingerprints = null);

public sealed record GitRepositoryConnectionResponse(
    Guid Id,
    Guid OrganizationId,
    string Name,
    string Provider,
    string CloneUrl,
    string PermittedRepositoryPath,
    string AuthenticationMode,
    bool CanReadFetch,
    bool CanPushTicketBranch,
    string DefaultBranch,
    string PullRequestProvider,
    IReadOnlyList<string> AllowedHosts,
    IReadOnlyList<int> AllowedPorts,
    IReadOnlyList<string> SshHostFingerprints,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record GrantGitRepositoryConnectionRequest(
    Guid AgentInstallationId,
    bool CanReadFetch = true,
    bool CanPushTicketBranch = false);

/// <summary>
/// Sets one encrypted credential component. Values are write-only and never appear
/// in a response or audit payload.
/// </summary>
public sealed record SetGitRepositoryCredentialRequest(
    Guid AgentInstallationId,
    [property: Required, MaxLength(80)] string Component,
    [property: Required, MaxLength(65536)] string Value);

public sealed record AssignSoftwareDevelopmentWorkItemRequest(
    Guid AssignedInstallationId,
    SoftwareDevelopmentBrief Development,
    long ExpectedRevision,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record UnassignSoftwareDevelopmentWorkItemRequest(
    long ExpectedRevision,
    [property: Required, MaxLength(160)] string IdempotencyKey);
