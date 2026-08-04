using System.ComponentModel.DataAnnotations;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Contracts.WorkManagement;

public sealed record SourceControlRepositoryOptionResponse(
    Guid Id,
    Guid OrganizationId,
    string Name,
    string Provider,
    string CanonicalPath,
    string DefaultBranch,
    string DeliveryKind,
    bool IsManaged);

public sealed record AssignSoftwareDevelopmentWorkItemRequest(
    Guid AssignedInstallationId,
    SoftwareDevelopmentBrief Development,
    long ExpectedRevision,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record UnassignSoftwareDevelopmentWorkItemRequest(
    long ExpectedRevision,
    [property: Required, MaxLength(160)] string IdempotencyKey);
