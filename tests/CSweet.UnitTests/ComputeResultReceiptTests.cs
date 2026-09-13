using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Infrastructure.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeResultReceiptTests
{
    [Fact]
    public async Task Expired_completed_result_has_exact_receipt_without_reapplying_or_observing()
    {
        await using var f = new ComputeResultTests.Fixture(); await f.InitializeAsync();
        var signed = f.Sign(); await f.Reconciler.ApplyAsync(signed, default);
        var clock = new ComputeDispatchTests.Clock { Now = f.Result.ExpiresAt.AddHours(1) };
        var service = new ComputeProviderWorkService(f.Broker.Db, new(f.Enrollment, clock), null!, f.Broker.Ledger, clock);
        var query = new ComputeProviderWorkRequest(f.Result.OrganizationId, f.Result.NodeId, f.Result.ProviderId, Guid.NewGuid(), clock.Now,
            clock.Now.AddMinutes(1), f.Result.OperationId, ResultSequence: f.Result.Sequence, ResultDigest: ComputeProtocol.Digest(signed.PayloadJson));
        var operation = await f.Broker.Db.ComputeOperations.SingleAsync(); var revision = operation.Revision;
        var receipt = await service.ReadResultReceiptAsync(Sign(query, f.Key), default);
        Assert.NotNull(receipt); Assert.False(receipt.Applied); Assert.Equal(query.ResultDigest, receipt.PayloadDigest);
        Assert.Equal(revision, operation.Revision); Assert.Equal("Completed", operation.Status);
        Assert.True((await f.Broker.Db.ComputeEnvironments.SingleAsync()).HoldsReservation);
        Assert.Null(await service.ReadResultReceiptAsync(Sign(query with { ResultDigest = "sha256:" + new string('0', 64) }, f.Key), default));
        var completed = await service.ReadResultReceiptAsync(Sign(query with { ResultSequence = 2 }, f.Key), default);
        Assert.NotNull(completed); Assert.Equal(ComputeResultDisposition.Completed, completed.Disposition);
        Assert.Equal(revision, operation.Revision); Assert.Equal(1, operation.LastResultSequence);
    }

    [Fact]
    public async Task Receipt_query_cannot_be_used_as_claim_or_disclose_another_nodes_recorded_result()
    {
        await using var f = new ComputeResultTests.Fixture(); await f.InitializeAsync(); var signed = f.Sign(); await f.Reconciler.ApplyAsync(signed, default);
        var clock = new ComputeDispatchTests.Clock();
        var query = new ComputeProviderWorkRequest(f.Result.OrganizationId, Guid.NewGuid(), f.Result.ProviderId, Guid.NewGuid(), clock.Now,
            clock.Now.AddMinutes(1), f.Result.OperationId, ResultSequence: 1, ResultDigest: ComputeProtocol.Digest(signed.PayloadJson));
        var trust = new ComputeResultTests.Trust(new("key-1", query.OrganizationId, query.NodeId, query.ProviderId, Convert.ToBase64String(f.Key.ExportSubjectPublicKeyInfo())));
        var service = new ComputeProviderWorkService(f.Broker.Db, new(trust, clock), null!, f.Broker.Ledger, clock);
        Assert.Null(await service.ReadResultReceiptAsync(Sign(query, f.Key), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ClaimAsync(Sign(query, f.Key), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadResultReceiptAsync(Sign(query with { ResultDigest = null }, f.Key), default));
        trust.Revoked = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadResultReceiptAsync(Sign(query, f.Key), default));
    }

    [Fact]
    public async Task Superseded_unrecorded_result_receives_explicit_disposition_without_adopting_state_or_releasing_capacity()
    {
        await using var f = new ComputeResultTests.Fixture(); await f.InitializeAsync();
        await f.Broker.Broker.ChangeLifecycleAsync(f.Broker.Organization, f.Broker.Installation,
            new(f.Result.EnvironmentId, 1, InfrastructureActions.Destroy, "replace-provision"), default);
        var operation = await f.Broker.Db.ComputeOperations.SingleAsync(x => x.Id == f.Result.OperationId);
        Assert.Equal("Superseded", operation.Status); Assert.Equal(0, operation.LastResultSequence);
        var clock = new ComputeDispatchTests.Clock();
        var service = new ComputeProviderWorkService(f.Broker.Db, new(f.Enrollment, clock), null!, f.Broker.Ledger, clock);
        var query = new ComputeProviderWorkRequest(f.Result.OrganizationId, f.Result.NodeId, f.Result.ProviderId,
            Guid.NewGuid(), clock.Now, clock.Now.AddMinutes(1), f.Result.OperationId, ResultSequence: 1,
            ResultDigest: ComputeProtocol.Digest(f.Sign().PayloadJson));
        var receipt = await service.ReadResultReceiptAsync(Sign(query, f.Key), default);
        Assert.NotNull(receipt); Assert.Equal(ComputeResultDisposition.Superseded, receipt.Disposition); Assert.False(receipt.Applied);
        Assert.Equal(query.ResultDigest, receipt.PayloadDigest); Assert.Equal(0, operation.LastResultSequence); Assert.Null(operation.LastResultDigest);
        Assert.True((await f.Broker.Db.ComputeEnvironments.SingleAsync()).HoldsReservation);
        var audit = f.Broker.Ledger.Events.Last(x => x.EventType == "compute.provider-result.receipt-read.v1");
        using var metadata = JsonDocument.Parse(audit.MetadataJson!);
        Assert.Equal((int)ComputeResultDisposition.Superseded, metadata.RootElement.GetProperty("Disposition").GetInt32());
    }
    [Fact]
    public async Task Completed_status_without_recorded_completion_cannot_discard_pending_evidence()
    {
        await using var f = new ComputeResultTests.Fixture(); await f.InitializeAsync();
        var operation = await f.Broker.Db.ComputeOperations.SingleAsync();
        operation.Status = "Completed"; operation.CompletedAt = new ComputeBrokerTests.Clock().GetUtcNow();
        await f.Broker.Db.SaveChangesAsync();
        var clock = new ComputeDispatchTests.Clock();
        var service = new ComputeProviderWorkService(f.Broker.Db, new(f.Enrollment, clock), null!, f.Broker.Ledger, clock);
        var query = new ComputeProviderWorkRequest(f.Result.OrganizationId, f.Result.NodeId, f.Result.ProviderId, Guid.NewGuid(), clock.Now,
            clock.Now.AddMinutes(1), f.Result.OperationId, ResultSequence: 2, ResultDigest: ComputeProtocol.Digest(f.Sign().PayloadJson));
        Assert.Null(await service.ReadResultReceiptAsync(Sign(query, f.Key), default));
        Assert.True((await f.Broker.Db.ComputeEnvironments.SingleAsync()).HoldsReservation);
    }
    private static SignedComputeProviderWorkRequest Sign(ComputeProviderWorkRequest query, ECDsa key)
    {
        var json = JsonSerializer.Serialize(query, ComputeProtocol.Json);
        return new("key-1", json, Convert.ToBase64String(key.SignData(ComputeProtocol.WorkRequestPayload(json), HashAlgorithmName.SHA256)));
    }
}
