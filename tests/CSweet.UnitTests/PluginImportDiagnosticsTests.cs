using System.Net;
using System.Text.Json;
using CSweet.ConnectionService.Protocol;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class PluginImportDiagnosticsTests
{
    [Fact]
    public async Task FailedAttemptResumeReceiptAndWorkerResultHaveDistinctDurableRevisionsAndOneCorrelation()
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create();
        var (gateway, setupId) = await BeginManaged(f);
        gateway.DiscoveryError = new HttpRequestException("CANARY token=private-code", null, HttpStatusCode.ServiceUnavailable);
        await f.Service.ImportAsync(setupId, default);
        var failed = await Setup(f, setupId); var failure = Assert.IsType<PluginAccountImportProgress>(failed.Import);
        Assert.Equal("Failed", failure.State); Assert.Equal("ManagedDiscovery", failure.Stage);
        Assert.Equal(503, failure.HttpStatus); Assert.Equal("ManagedService", failure.HttpStatusSource);
        Assert.Equal("HttpRequestException", failure.ExceptionType); Assert.Equal("http_error", failure.FailureKind);
        Assert.Equal(1, failure.Attempt); Assert.NotNull(failure.StartedAt); Assert.NotNull(failure.FinishedAt);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(failed));
        await f.Service.ProcessPendingAsync(default);
        Assert.Equal(1, gateway.Discoveries); // Failed imports do not automatically resubmit.

        var receipt = await f.Service.ResumeImportAsync(f.Admin, f.Id, setupId, failed.Revision, default);
        var queued = await Setup(f, setupId);
        Assert.Equal(receipt.Revision, queued.Revision); Assert.True(receipt.Revision > failed.Revision);
        Assert.Equal(receipt.CorrelationId, queued.Import!.CorrelationId); Assert.NotEqual(failure.CorrelationId, receipt.CorrelationId);
        Assert.Equal("Queued", queued.Import.State); Assert.Equal(0, queued.Import.Attempt);
        Assert.Null(queued.Import.FailureKind); Assert.Null(queued.Import.HttpStatus); Assert.Null(queued.Import.StartedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ResumeImportAsync(f.Admin, f.Id, setupId, failed.Revision, default));
        Assert.Equal(receipt.Revision, (await Setup(f, setupId)).Revision);
        Assert.Equal(1, gateway.Discoveries);

        gateway.DiscoveryError = null;
        await f.Service.ProcessPendingAsync(default);
        var completed = await Setup(f, setupId);
        Assert.Equal("Ready", completed.Status); Assert.True(completed.Revision > receipt.Revision);
        Assert.Equal(receipt.CorrelationId, completed.Import!.CorrelationId); Assert.Equal("Completed", completed.Import.State);
        Assert.Equal(1, completed.Import.Attempt); Assert.NotNull(completed.Import.FinishedAt);
        Assert.Null(completed.Import.FailureKind); Assert.Null(completed.Import.HttpStatus);
        Assert.Equal(2, gateway.Discoveries);
        Assert.Empty(await f.Inner.Db.PluginAccountBusinessGrants.ToArrayAsync());
    }

    [Fact]
    public async Task StaleFailedViewCannotQueueAnotherAttemptAfterNewAttemptAlsoFails()
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create(); var (gateway, id) = await BeginManaged(f);
        gateway.DiscoveryError = new JsonException("CANARY raw provider body");
        await f.Service.ImportAsync(id, default); var old = await Setup(f, id);
        var accepted = await f.Service.ResumeImportAsync(f.Admin, f.Id, id, old.Revision, default);
        await f.Service.ImportAsync(id, default); var latest = await Setup(f, id);
        Assert.Equal("ImportFailed", latest.Status); Assert.True(latest.Revision > accepted.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ResumeImportAsync(f.Admin, f.Id, id, old.Revision, default));
        Assert.Equal(latest.Revision, (await Setup(f, id)).Revision); Assert.Equal(2, gateway.Discoveries);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(latest));
    }

    [Fact]
    public async Task DiagnosticsAndResumeRemainRestrictedToAuthorizedHumansAndInstallation()
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create(); var (gateway, id) = await BeginManaged(f);
        gateway.DiscoveryError = new HttpRequestException(); await f.Service.ImportAsync(id, default);
        var before = await Setup(f, id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.GetAsync(f.Manager, f.Id, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ResumeImportAsync(f.Manager, f.Id, id, before.Revision, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ResumeImportAsync(f.Admin, Guid.NewGuid(), id, before.Revision, default));
        Assert.Equal(before.Revision, (await Setup(f, id)).Revision); Assert.Equal(1, gateway.Discoveries);
    }

    [Fact]
    public async Task CancellationRecordsInterruptionWithoutStartingAnotherAttemptOrReplacingKeys()
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create(); var (gateway, id) = await BeginManaged(f);
        var keys = f.Secrets.Values.Keys.Order().ToArray(); using var cancel = new CancellationTokenSource();
        gateway.BeforeDiscovery = _ => { cancel.Cancel(); return Task.FromCanceled(cancel.Token); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ImportAsync(id, cancel.Token));
        var interrupted = await Setup(f, id);
        Assert.Equal("Importing", interrupted.Status); Assert.Equal("Interrupted", interrupted.Import!.State);
        Assert.Equal("OperationCanceledException", interrupted.Import.ExceptionType); Assert.Equal("interrupted", interrupted.Import.FailureKind);
        Assert.NotNull(interrupted.Import.FinishedAt); Assert.Equal(keys, f.Secrets.Values.Keys.Order());
        await f.Service.ProcessPendingAsync(default); Assert.Equal(1, gateway.Discoveries); // Existing unexpired lease fences recovery.
        Assert.Empty(await f.Inner.Db.PluginAccountResources.ToArrayAsync());
    }

    [Fact]
    public async Task DiagnosticFailureCannotOverwriteConcurrentCancellation()
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create(); var (gateway, id) = await BeginManaged(f);
        gateway.BeforeDiscovery = async _ =>
        {
            var session = await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(x => x.Id == id);
            session.Status = "Cancelled"; session.LeaseOwner = null; session.Revision++;
            await f.Inner.Db.SaveChangesAsync();
        };
        gateway.DiscoveryError = new HttpRequestException("CANARY");
        await f.Service.ImportAsync(id, default);
        var sessionAfter = await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(x => x.Id == id);
        Assert.Equal("Cancelled", sessionAfter.Status); Assert.Null(sessionAfter.LeaseOwner);
        Assert.Null(sessionAfter.FailureCode); Assert.Empty(await f.Inner.Db.PluginAccountResources.ToArrayAsync());
    }

    [Theory]
    [InlineData("broken-json")]
    [InlineData("unsafe-stage")]
    [InlineData("unsafe-type")]
    [InlineData("oversized")]
    public async Task StoredMalformedOrUntrustedDiagnosticTextIsNeverReturned(string variant)
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create(); var (_, id) = await BeginManaged(f);
        var safe = new PluginAccountImportProgress(Guid.NewGuid(), "Failed", "ManagedDiscovery", 1, ExceptionType: "HttpRequestException");
        var record = await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(x => x.Id == id);
        record.ImportDiagnosticsJson = variant switch
        {
            "broken-json" => "CANARY invalid json",
            "oversized" => new string('x', 2049) + "CANARY",
            "unsafe-stage" => JsonSerializer.Serialize(safe with { Stage = "CANARY raw provider body" }),
            _ => JsonSerializer.Serialize(safe with { ExceptionType = "CANARY.CustomTokenType" })
        };
        await f.Inner.Db.SaveChangesAsync();
        var view = await Setup(f, id); Assert.Null(view.Import); Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(view));
    }

    [Fact]
    public async Task CustomerOwnedHttpStatusIsProviderScopedAndResponseBodyIsExcluded()
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create(); var begin = await f.Begin(); await f.Complete(begin);
        f.Transport.ResourceStatus = 403; await f.Service.ImportAsync(begin.SetupId, default);
        var setup = await Setup(f, begin.SetupId); Assert.Equal("ImportFailed", setup.Status);
        Assert.Equal("ResourceRead", setup.Import!.Stage); Assert.Equal(403, setup.Import.HttpStatus);
        Assert.Equal("Provider", setup.Import.HttpStatusSource); Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(setup));
    }

    [Fact]
    public async Task LegacySessionStartsWithNoInventedAttemptEvidence()
    {
        await using var f = await PluginAccountFlowTests.Fixture.Create(); var (_, id) = await BeginManaged(f);
        var view = await Setup(f, id); Assert.True(view.Revision > 0); Assert.Null(view.Import);
    }

    private static async Task<(ManagedPluginAccountFlowTests.Gateway Gateway, Guid SetupId)> BeginManaged(PluginAccountFlowTests.Fixture f)
    {
        using var document = JsonDocument.Parse(f.Inner.Connector.PackageVersion!.ManifestJson);
        var gateway = new ManagedPluginAccountFlowTests.Gateway(document.RootElement.GetProperty("connections")[0].GetProperty("providerProfile").GetString()!);
        f.Managed = gateway; var binding = DeploymentProofCodec.Random();
        var begin = await f.Service.BeginAsync(f.Admin, f.Id, new(["base"], "v1", true, f.Digest), default, binding);
        await f.Service.CompleteManagedAsync(f.Admin, begin.SetupId, new string('h', 43), binding, default);
        return (gateway, begin.SetupId);
    }
    private static async Task<PluginAccountSetupView> Setup(PluginAccountFlowTests.Fixture f, Guid id) =>
        (await f.Service.GetAsync(f.Admin, f.Id, default)).SetupSessions.Single(x => x.Id == id);
}
