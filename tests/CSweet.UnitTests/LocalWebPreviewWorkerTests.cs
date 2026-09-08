using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Api.Core;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Office.Contracts.Workloads;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class LocalWebPreviewWorkerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingPreviewBecomesARealHttpLinkOrFailsForUnverifiedOutput(bool corrupt)
    {
        var clock = new PreviewClock();
        var bytes = Encoding.UTF8.GetBytes("<html><canvas>Playable build</canvas></html>");
        using var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, leaveOpen: true))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "payload/output/index.html") { DataStream = new MemoryStream(bytes) });
        var services = new ServiceCollection();
        var database = Guid.NewGuid().ToString("N");
        services.AddDbContext<CSweetDbContext>(options => options.UseInMemoryDatabase(database));
        services.AddSingleton<IAgentArtifactStore>(new Store(archive.ToArray()));
        using var provider = services.BuildServiceProvider();
        var organization = Guid.NewGuid();
        var stream = Guid.NewGuid();
        var build = Guid.NewGuid();
        var preview = Guid.NewGuid();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            db.DeliveryBuilds.Add(new() { Id = build, OrganizationId = organization, WorkstreamId = stream, Status = "Succeeded",
                OutputsJson = JsonSerializer.Serialize(new[] { new BuildOutputManifestEntry("index.html",
                    corrupt ? new string('0',64) : Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, "text/html", "web-file") }) });
            db.ExecutionWorkloadAssignments.Add(new() { Id = Guid.NewGuid(), DeliveryBuildId = build,
                WorkloadKind = ExecutionWorkloadKind.ToolchainBuild, ResultArtifactDigest = "bundle" });
            db.PreviewSessions.Add(new() { Id = preview, OrganizationId = organization, WorkstreamId = stream, BuildId = build,
                Mode = "web-static", Status = "Requested", ExpiresAt = clock.Now.AddMinutes(5) });
            await db.SaveChangesAsync();
        }
        using var worker = new LocalWebPreviewWorker(provider.GetRequiredService<IServiceScopeFactory>(), clock,
            NullLogger<LocalWebPreviewWorker>.Instance);
        try
        {
            await worker.ReconcileAsync(CancellationToken.None);
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var record = await db.PreviewSessions.AsNoTracking().SingleAsync();
            Assert.Equal(corrupt ? "Failed" : "Ready", record.Status);
            if (corrupt) Assert.Null(record.AccessReference);
            else
            {
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
                Assert.Equal(bytes, await client.GetByteArrayAsync(record.AccessReference));
                await worker.ReconcileAsync(CancellationToken.None);
                Assert.Equal(record.AccessReference, (await db.PreviewSessions.AsNoTracking().SingleAsync()).AccessReference);
            }
        }
        finally
        {
            clock.Now = clock.Now.AddMinutes(6);
            await worker.ReconcileAsync(CancellationToken.None);
        }
        using var finalScope = provider.CreateScope();
        var final = await finalScope.ServiceProvider.GetRequiredService<CSweetDbContext>().PreviewSessions.SingleAsync();
        Assert.Null(final.AccessReference);
        Assert.Equal(corrupt ? "Failed" : "Expired", final.Status);
    }

    private sealed class Store(byte[] bytes) : IAgentArtifactStore
    {
        public Task<bool> ExistsAsync(string digest, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<Stream> OpenReadAsync(string digest, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream(bytes));
        public Task<AgentArtifactReference> ImportAsync(Stream content, ArtifactImportDescriptor descriptor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class PreviewClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
