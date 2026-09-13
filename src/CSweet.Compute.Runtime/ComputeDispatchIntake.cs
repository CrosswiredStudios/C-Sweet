using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

public sealed record ComputeDispatchIntakePass(int Discovered, int Executed, int Deferred, Guid? NextAfterOperationId);

/// <summary>A bounded discovery pass for startup, durable work hints and reconnect recovery.</summary>
public sealed class ComputeDispatchIntake(ComputeDispatchVerifier verifier)
{
    public async Task<ComputeDispatchIntakePass> RunOnceAsync(Guid? afterOperationId,
        Func<Guid?, CancellationToken, Task<ComputeProviderWorkPage>> discover,
        Func<Guid, CancellationToken, Task<ComputeDispatchPacket?>> claim,
        Func<ComputeDispatchPacket, CancellationToken, Task> execute, CancellationToken token)
    {
        if (afterOperationId == Guid.Empty) throw new ArgumentException("A discovery cursor must identify an operation.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        linked.Token.ThrowIfCancellationRequested();
        var page = await discover(afterOperationId, linked.Token);
        if (page?.OperationIds is not { Count: <= 100 } ids || ids.Any(x => x == Guid.Empty ||
                afterOperationId.HasValue && x.CompareTo(afterOperationId.Value) <= 0) ||
            !ids.SequenceEqual(ids.Distinct().Order()) ||
            page.NextAfterOperationId != (ids.Count == 100 ? ids[^1] : (Guid?)null))
            throw new InvalidDataException("Provider discovery returned an invalid bounded page.");
        // Snapshot the untrusted page before awaiting claims. Discovery never grants execution.
        var operations = ids.ToArray(); var cursor = page.NextAfterOperationId;
        var executed = 0; var deferred = 0;
        foreach (var operationId in operations)
        {
            linked.Token.ThrowIfCancellationRequested();
            var packet = await claim(operationId, linked.Token);
            if (packet is null) { deferred++; continue; }
            var verified = verifier.Verify(packet);
            if (verified.Authorization.OperationId != operationId)
                throw new UnauthorizedAccessException("The dispatch does not match discovered work.");
            linked.Token.ThrowIfCancellationRequested();
            // Preserve Execute/Observe exactly. The executor verifies again and applies its durable fence.
            await execute(new(packet.Authorization, verified.Specification, verified.Template, verified.Workload), linked.Token);
            executed++;
        }
        // Exceptions are not converted to success: failed journal/physical work needs recovery.
        return new(operations.Length, executed, deferred, cursor);
    }
}
