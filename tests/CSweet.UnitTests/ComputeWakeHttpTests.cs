using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CSweet.Api.Compute;
using CSweet.Compute.Runtime;
using CSweet.Infrastructure.Compute;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UnitTests;

public sealed class ComputeWakeHttpTests
{
    [Fact]
    public async Task Authenticated_https_notification_wakes_intake_without_claiming_work()
    {
        await using var f = new ComputeMaintenanceIngestorTests.Fixture(); await f.InitializeAsync();
        var clock = f.Provider.Journal.Core.Time;
        var work = new ComputeProviderWorkService(f.Db, new(f.Trust, clock), f.Provider.Journal.Core.Authorizer(),
            f.Provider.Journal.Core.Broker.Ledger, clock);
        var channels = new ComputeProviderWakeChannels();
        await using var server = new ComputeMaintenanceHttpTests.Server(f, services =>
        { services.AddSingleton(work); services.AddSingleton(channels); });
        await server.StartAsync();
        using var certificate = new CertificateRequest("CN=compute-wake-test", f.Key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(clock.Now.AddDays(-1), clock.Now.AddDays(1));
        using var client = new ComputeWorkHttpClient(server.Https,
            new(f.Provider.Journal.Enrollment, certificate, "node-key", clock), f.Provider.Verifier, server.Handler());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var woke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new ComputeProviderNotificationWorker(client.WaitForWakeAsync, () => woke.TrySetResult(), clock, _ => { });
        var claimsBefore = f.Provider.Journal.Core.Signing.Claims.Count;
        var run = notifications.RunAsync(stop.Token);
        var enrollment = f.Provider.Journal.Enrollment;
        try
        {
            // Wait for the actual HTTPS request to authenticate and install its server-side waiter.
            var id = Guid.NewGuid();
            while (!await channels.PublishAsync(enrollment.OrganizationId, enrollment.NodeId, enrollment.ProviderId, id, stop.Token))
                await Task.Delay(10, stop.Token);
            await woke.Task.WaitAsync(stop.Token);
            Assert.Equal(claimsBefore, f.Provider.Journal.Core.Signing.Claims.Count);
            Assert.False(server.SawCookie); Assert.Equal(0, server.Captures);
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        Assert.False(await channels.PublishAsync(enrollment.OrganizationId, enrollment.NodeId, enrollment.ProviderId, Guid.NewGuid(), default));
    }
}
