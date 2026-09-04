using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Application.Infrastructure;

/// <summary>
/// Trusted provider boundary. The agent supplies only a manifest-declared capability and typed input;
/// credentials, transport sessions, endpoint selection, and result redaction stay inside C-Sweet.
/// </summary>
public interface IInfrastructureProviderGateway
{
    Task<JsonElement> InvokeAsync(
        Guid organizationId,
        Guid installationId,
        string capability,
        JsonElement input,
        CancellationToken cancellationToken = default);

    Task<JsonElement> InvokeApprovedAsync(
        Guid organizationId,
        Guid installationId,
        string capability,
        JsonElement input,
        CancellationToken cancellationToken = default);

    Task<InfrastructureFileTransferResponse> TransferAsync(
        Guid organizationId,
        Guid installationId,
        InfrastructureFileTransferRequest request,
        CancellationToken cancellationToken = default);
}

public interface IInfrastructureChangeExecutionService
{
    Task<IReadOnlyList<InfrastructureOperationReceipt>> ExecuteAsync(
        CSweet.Domain.Core.ActionProposal proposal,
        CancellationToken cancellationToken = default);
}

public sealed record InfrastructureChangeEnvelope(
    string ChannelId,
    string ActionType,
    string PayloadHash,
    string IdempotencyKey,
    string? ResourceId,
    long? ExpectedRevision,
    bool AlwaysRequiresApproval,
    InfrastructureChangeProposalRequest Change,
    IReadOnlyList<InfrastructureApprovalStage> ApprovalRoute);
