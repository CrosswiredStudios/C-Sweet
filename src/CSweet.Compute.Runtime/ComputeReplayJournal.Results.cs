using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;

namespace CSweet.Compute.Runtime;

/// <summary>Trusted backend evidence, not an agent-supplied desired state.</summary>
public sealed record ComputeLifecycleObservation(ComputeLifecycleState State, bool TeardownConfirmed = false, string? FailureCode = null, ComputeWorkloadResult? Workload = null);
public sealed record ComputeResultOutboxEntry(ComputeProviderResult Result, string Digest);

public sealed partial class ComputeReplayJournal
{
    public Task<IReadOnlyList<ComputeResultOutboxEntry>> ListResultsAsync(int limit, CancellationToken token) =>
        ListResultsAsync(null, limit, token);

    public async Task<IReadOnlyList<ComputeResultOutboxEntry>> ListResultsAsync(Guid? afterOperationId, int limit, CancellationToken token)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var held = await LockAsync(token);
        var state = await ReadAsync(token);
        return state.ResultOutbox.Values.Where(x => !afterOperationId.HasValue || x.Result.OperationId.CompareTo(afterOperationId.Value) > 0)
            .OrderBy(x => x.Result.OperationId).Take(limit).ToArray();
    }

    public async Task<bool> AcknowledgeResultAsync(ComputeResultAcknowledgement acknowledgement, CancellationToken token)
    {
        await using var held = await LockAsync(token);
        var state = await ReadAsync(token);
        if (!state.ResultOutbox.TryGetValue(acknowledgement.OperationId, out var row)) return false;
        // A delivery can race a newer observation. Never remove its replacement.
        if (acknowledgement.Sequence < row.Result.Sequence) return false;
        if (acknowledgement.Sequence != row.Result.Sequence || acknowledgement.PayloadDigest != row.Digest)
            throw new InvalidDataException("Result acknowledgement does not match queued evidence.");
        state.ResultOutbox.Remove(acknowledgement.OperationId);
        await WriteAsync(state, token);
        return true;
    }

    private void StageResult(JournalState state, EnvironmentHistory environment, OperationHistory operation,
        ComputeDispatchAuthorization claim, ComputeLifecycleObservation observation)
    {
        if (!AllowedResultState(claim.Action, observation.State) ||
            observation.TeardownConfirmed != (observation.State == ComputeLifecycleState.Destroyed) ||
            observation.TeardownConfirmed && claim.Action != InfrastructureActions.Destroy ||
            observation.State is ComputeLifecycleState.Ready or ComputeLifecycleState.Busy or ComputeLifecycleState.Stopped && environment.ResourceId is null ||
            observation.FailureCode is { } failure && !ComputeSpecification.Identifier(failure))
            throw new InvalidDataException("Physical lifecycle evidence is invalid for this operation.");
        // A command receipt is immutable evidence; recovery must never repeat its side effect.
        if (claim.Action == InfrastructureActions.Execute && observation.Workload?.ErrorCode == "outcome-unknown" &&
            state.ResultOutbox.TryGetValue(claim.OperationId, out var prior) && prior.Result.Workload is { } recorded)
            observation = observation with { Workload = recorded };
        var now = clock.GetUtcNow();
        var sequence = checked(operation.LastResultSequence + 1);
        var result = new ComputeProviderResult(claim.OperationId, claim.EnvironmentId, claim.OrganizationId,
            claim.InstallationId, claim.ProviderId, claim.NodeId, claim.Generation, claim.Action, claim.SpecificationDigest,
            sequence, environment.ResourceId, observation.State, observation.TeardownConfirmed, now, now.AddMinutes(2), observation.FailureCode, observation.Workload);
        if (observation.TeardownConfirmed) environment.TeardownConfirmed = true;
        operation.LastResultSequence = sequence;
        state.ResultOutbox[claim.OperationId] = new(result, ComputeProtocol.Digest(JsonSerializer.Serialize(result, ComputeProtocol.Json)));
    }

    private void ValidateResultOutbox(JournalState state)
    {
        if (state.Environments.Values.Any(x => x.Operations.Values.Any(o => o.LastResultSequence < 0)))
            throw new InvalidDataException("Protected result sequence is invalid.");
        foreach (var (id, row) in state.ResultOutbox)
        {
            if (row?.Result is not { } result || id != result.OperationId || result.NodeId != enrollment.NodeId ||
                result.OrganizationId != enrollment.OrganizationId || result.ProviderId != enrollment.ProviderId ||
                !state.Environments.TryGetValue(result.EnvironmentId, out var environment) ||
                result.InstallationId != environment.InstallationId || result.SpecificationDigest != environment.SpecificationDigest ||
                !environment.Operations.TryGetValue(id, out var operation) || result.Sequence < 1 ||
                result.Sequence != operation.LastResultSequence || result.Generation != operation.Generation || result.Action != operation.Action ||
                result.ResourceId is not null && result.ResourceId != environment.ResourceId || !AllowedResultState(result.Action, result.State) ||
                result.TeardownConfirmed != (result.State == ComputeLifecycleState.Destroyed) ||
                result.ExpiresAt != result.ObservedAt.AddMinutes(2) ||
                row.Digest != ComputeProtocol.Digest(JsonSerializer.Serialize(result, ComputeProtocol.Json)))
                throw new InvalidDataException("Protected result outbox is invalid.");
        }
    }

    private static bool AllowedResultState(string action, ComputeLifecycleState state) => action switch
    {
        InfrastructureActions.Provision or InfrastructureActions.Start or InfrastructureActions.Restart => state is
            ComputeLifecycleState.Provisioning or ComputeLifecycleState.Bootstrapping or ComputeLifecycleState.Ready or ComputeLifecycleState.Busy or ComputeLifecycleState.Failed,
        InfrastructureActions.Execute or InfrastructureActions.PublishPort => state is ComputeLifecycleState.Ready or ComputeLifecycleState.Failed,
        InfrastructureActions.Stop => state is ComputeLifecycleState.Stopping or ComputeLifecycleState.Stopped or ComputeLifecycleState.Failed,
        InfrastructureActions.Destroy => state is ComputeLifecycleState.Destroying or ComputeLifecycleState.Destroyed or ComputeLifecycleState.Failed,
        _ => false
    };
}
