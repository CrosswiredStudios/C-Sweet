using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class PlatformLlmCapabilityHandlerTests
{
    // Opt-in real inference through the production adapter and broker, using a new
    // PostgreSQL database. No installation, task or provider in the application is changed.
    [StreamLiveFact]
    public async Task Live_provider_stream_completes_with_durable_ordered_audit_and_bounded_tracking()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("CSWEET_STREAM_TEST_PROVIDER")!);
        Assert.True(endpoint.IsLoopback, "Live stream verification requires a loopback provider.");
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_STREAM_TEST_DATABASE"));
        Assert.Contains(settings.Host, new[] { "localhost", "127.0.0.1", "::1" });
        settings.Database = $"csweet_stream_test_{Guid.NewGuid():N}";
        var probe = new StreamPersistenceProbe(streamOutput);
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(settings.ConnectionString)
            .AddInterceptors(probe).Options;
        var protection = new EphemeralDataProtectionProvider();
        await using var db = new CSweetDbContext(options, protection);
        try
        {
            await db.Database.MigrateAsync();
            var provider = await AddProviderAsync(db);
            var profile = await db.LlmProviderProfiles.SingleAsync();
            profile.BaseUrl = endpoint.AbsoluteUri;
            profile.DefaultChatModel = Environment.GetEnvironmentVariable("CSWEET_STREAM_TEST_MODEL")!;
            profile.SupportsStreaming = true;
            var session = new AgentSession(Guid.NewGuid().ToString("N"), "stream-verification", Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(),
                    new HashSet<string>([PlatformCapabilities.LlmChatStream]), 1));
            db.AgentInstallations.Add(new AgentInstallation
            {
                Id = Guid.Parse(session.InstallationId), BusinessId = session.BusinessId, InstallationKey = Guid.NewGuid(),
                PackageVersion = new AgentPackageVersion
                {
                    Id = Guid.NewGuid(), AgentId = session.AgentId, Version = "1.0.0", ManifestJson = "{}",
                    PackageSource = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.invalid/stream-test" }
                },
                Grant = new AgentInstallationGrant
                {
                    Id = Guid.NewGuid(), AgentInstallationId = Guid.Parse(session.InstallationId), GrantRevision = 1,
                    RequiredCapabilitiesJson = JsonSerializer.Serialize(session.Grant.RequestedCapabilities)
                }
            });
            await db.SaveChangesAsync();
            await SeedDispatchAuthorityAsync(db, session);
            db.AgentInstallationConfigurations.Add(new AgentInstallationConfiguration
            {
                Id = Guid.NewGuid(), AgentInstallationId = Guid.Parse(session.InstallationId),
                SettingsJson = "{\"maxContextWindowTokens\":32768,\"maxOutputTokens\":4096}"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var factory = new OpenAiCompatibleLlmProviderFactory(db, new InMemoryLlmProviderSecretStore(),
                NullLogger<OpenAiCompatibleLlmProviderFactory>.Instance);
            var handler = new PlatformLlmCapabilityHandler(db, factory, new AgentEmployeeIdentityResolver(db),
                new AgentInstallationConfigurationService(db, new TestAuditEventWriter()), [], new TestMediaAssetService(),
                NullLogger<PlatformLlmCapabilityHandler>.Instance);
            var request = new RequestCapability
            {
                RequestId = Guid.NewGuid().ToString("N"), Capability = PlatformCapabilities.LlmChatStream,
                Payload = JsonPayload.From(new
                {
                    providerProfileId = provider,
                    messages = new[] { new { role = "user", text = "This is an isolated streaming diagnostic. Write a numbered list of 40 short tips for naming variables in a small game. End your final answer with STREAM_OK. Do not call tools or modify files." } }
                })
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var timer = Stopwatch.StartNew();
            var results = new List<CapabilityResult>();
            await foreach (var result in handler.StreamAsync(session, request, timeout.Token)) results.Add(result);
            timer.Stop();
            Assert.All(results, x => Assert.True(x.Succeeded, x.Error));
            Assert.False(results[^1].HasMore);
            var chunks = results.Where(x => x.HasMore).Select(x => JsonSerializer.Deserialize<ObservedStreamChunk>(x.Payload.Span, JsonOptions)!).ToArray();
            Assert.Contains("STREAM_OK", string.Concat(chunks.Select(x => x.Text)));
            Assert.Contains(chunks, x => x.FinishReason == "stop");
            Assert.True(probe.PeakAuditRows < 32, $"Tracked {probe.PeakAuditRows} audit rows.");
            await using var verify = new CSweetDbContext(options, protection);
            var run = await verify.AgentRunLogs.SingleAsync();
            Assert.Equal("Completed", run.Status);
            Assert.NotNull(run.CompletedAt);
            Assert.True(run.ReportedOutputTokens > 0);
            var rows = await verify.AuditOutbox.AsNoTracking().Where(x => x.SourceEntityId == run.Id).ToListAsync();
            Assert.All(rows, x => { Assert.Equal("{}", x.RequestJson); Assert.NotNull(x.ProtectedRequest); });
            var receipts = rows.Select(x => JsonSerializer.Deserialize<AuditEventWriteRequest>(Encoding.UTF8.GetString(
                    protection.CreateProtector("CSweet.AuditOutbox.v1").Unprotect(x.ProtectedRequest!)))!)
                .Where(x => x.EventType == "model.response.chunk").ToArray();
            var sequences = receipts.Select(x =>
            {
                using var json = JsonDocument.Parse(x.Payload!.Value);
                return json.RootElement.GetProperty("sequence").GetInt32();
            }).OrderBy(x => x).ToArray();
            Assert.Equal(Enumerable.Range(0, chunks.Length), sequences);
            streamOutput.WriteLine($"LIVE model={profile.DefaultChatModel}; elapsed={timer.ElapsedMilliseconds} ms; " +
                $"outputTokens={run.ReportedOutputTokens}; chunks={chunks.Length}; durableReceipts={receipts.Length}; peakTrackedAuditRows={probe.PeakAuditRows}; status={run.Status}");
        }
        finally
        {
            // The database name was generated above, never supplied by the caller.
            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class StreamLiveFactAttribute : FactAttribute
    {
        public StreamLiveFactAttribute()
        {
            if (new[] { "CSWEET_STREAM_TEST_PROVIDER", "CSWEET_STREAM_TEST_MODEL", "CSWEET_STREAM_TEST_DATABASE" }
                .Any(x => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(x))))
                Skip = "Set CSWEET_STREAM_TEST_PROVIDER, CSWEET_STREAM_TEST_MODEL and CSWEET_STREAM_TEST_DATABASE for isolated local verification.";
        }
    }
}
