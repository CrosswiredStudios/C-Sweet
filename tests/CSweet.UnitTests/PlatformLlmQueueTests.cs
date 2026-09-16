using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class PlatformLlmQueueTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1024, true)]
    public async Task RequestByteLimitIsOptional(int limit, bool rejected)
    {
        Assert.Equal(0, new PlatformLlmJobOptions().MaximumRequestBytes);
        await using var fixture = await Fixture.CreateAsync(new() { MaximumRequestBytes = limit });
        var arguments = JsonSerializer.SerializeToElement(new
        {
            providerProfileId = Guid.NewGuid(),
            messages = new[] { new { role = "user", text = new string('x', 5 * 1024 * 1024) } }
        });
        var start = fixture.Service.StartAsync(fixture.First.Session, fixture.First.WorkId,
            1, "lease", "large-request", arguments, default);
        if (rejected)
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => start);
            Assert.Contains("1024-byte limit", error.Message);
            Assert.Equal(0, fixture.Executor.Started);
        }
        else
        {
            var jobId = await start;
            await fixture.Executor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Executor.ReleaseFirst.TrySetResult();
            Assert.Equal("Completed", (await fixture.WaitCompletedAsync(fixture.First, jobId)).GetProperty("state").GetString());
        }
    }

    [Fact]
    public async Task GenerationTimeoutPersistsFailureAndReleasesProviderForQueuedWork()
    {
        await using var fixture = await Fixture.CreateAsync(new() { GenerationTimeoutSeconds = 1 });
        var first = await fixture.StartAsync(fixture.First);
        await fixture.Executor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = await fixture.StartAsync(fixture.Second);
        var result = await fixture.WaitCompletedAsync(fixture.First, first);
        Assert.Equal("Failed", result.GetProperty("state").GetString());
        Assert.True(result.GetProperty("retryable").GetBoolean());
        Assert.Equal("llm.provider_unavailable", result.GetProperty("failureCode").GetString());
        Assert.Contains("generation time limit", result.GetProperty("error").GetString());
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var run = await db.AgentRunLogs.SingleAsync(x => x.Id == first);
        Assert.Equal("Failed", run.Status);
        Assert.NotNull(run.CompletedAt);
        await fixture.Executor.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Completed", (await fixture.WaitCompletedAsync(fixture.Second, second)).GetProperty("state").GetString());
        Assert.Equal(1, fixture.Executor.MaximumActive);
    }

    [Fact]
    public async Task ClaimedTicketDiscussionIsAddedToProviderContext()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var work = await db.AgentWorkItems.SingleAsync(x => x.Id == fixture.First.WorkId);
            var org = Guid.Parse(work.OrganizationId);
            var eventId = Guid.NewGuid();
            work.Kind = CSweet.Domain.Setup.AgentWorkKind.Event; work.SourceId = eventId.ToString();
            var board = new CSweet.Domain.WorkManagement.WorkBoard
            {
                Id = Guid.NewGuid(), OrganizationId = org, Kind = CSweet.Domain.WorkManagement.WorkBoardKind.Personal
            };
            var ticket = new CSweet.Domain.Core.WorkTask
            {
                Id = Guid.NewGuid(), OrganizationId = org, Board = board,
                Status = CSweet.Domain.Core.WorkTaskStatus.Running,
                AssignedAgentInstallationId = work.AgentInstallationId, ClaimEventId = eventId
            };
            db.CoreWorkTasks.Add(ticket);
            db.WorkItemComments.Add(new()
            {
                Id = Guid.NewGuid(), OrganizationId = org, WorkItemId = ticket.Id,
                AuthorDisplayName = "Manager", Body = "Use the existing deployment. The previous attempt timed out.",
                IdempotencyKey = "discussion"
            });
            await db.SaveChangesAsync();
        }
        var job = await fixture.StartAsync(fixture.First);
        await fixture.Executor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("Use the existing deployment.", fixture.Executor.LastRequestJson);
        Assert.Contains("untrusted discussion", fixture.Executor.LastRequestJson);
        Assert.DoesNotContain("Use the existing deployment.", fixture.Arguments.GetRawText());
        fixture.Executor.ReleaseFirst.TrySetResult();
        Assert.Equal("Completed", (await fixture.WaitCompletedAsync(fixture.First, job)).GetProperty("state").GetString());
    }

    [Fact]
    public void UnboundedPersonalWorkDeadlineDoesNotOverflowWhileWaitingForInference()
    {
        var deadline = new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);
        Assert.Equal(DateTimeOffset.MaxValue, PlatformLlmJobService.ExtendDeadline(deadline, TimeSpan.FromSeconds(5)));
        Assert.Equal(DateTimeOffset.MaxValue, PlatformLlmJobService.ExtendDeadline(DateTimeOffset.MaxValue, TimeSpan.FromSeconds(5)));
        var bounded = DateTimeOffset.UtcNow;
        Assert.Equal(bounded.AddMinutes(1), PlatformLlmJobService.ExtendDeadline(bounded, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task DatabaseFailureDoesNotStopEventDispatchAndShutdownStillWorks()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var attempts = 0;
        await AgentDispatchLoop.RunAsync(_ =>
        {
            if (++attempts == 1) throw new InvalidOperationException("Database recovery");
            stop.Cancel();
            return Task.CompletedTask;
        }, TimeProvider.System, NullLogger.Instance, stop.Token, TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task SingleSlotQueuesSecondRequestAndAcknowledgedWaitExtendsExactWorkBudget()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.StartAsync(fixture.First);
        await fixture.Executor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = await fixture.StartAsync(fixture.Second);
        Assert.Equal("Queued", (await fixture.ReadAsync(fixture.Second, second)).GetProperty("state").GetString());
        for (var i = 0; i < 6; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(30));
            await fixture.ReadAsync(fixture.First, first);
            await fixture.ReadAsync(fixture.Second, second);
        }
        var queued = await fixture.ReadAsync(fixture.Second, second);
        Assert.True(queued.GetProperty("workDeadline").GetDateTimeOffset() > fixture.Clock.GetUtcNow());
        Assert.Equal(1, fixture.Executor.Started);
        fixture.Executor.ReleaseFirst.TrySetResult();
        await fixture.Executor.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Executor.MaximumActive);
        await fixture.WaitCompletedAsync(fixture.Second, second);
    }

    [Fact]
    public async Task CancellationRemovesQueuedWorkAndRequestKeysAreIdempotentAndOwnerBound()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.StartAsync(fixture.First);
        await fixture.Executor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var queued = await fixture.StartAsync(fixture.Second);
        Assert.Equal(queued, await fixture.StartAsync(fixture.Second));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ReadAsync(fixture.First.Session, queued, 0, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.StartAsync(fixture.Second.Session,
            fixture.Second.Work, 1, "forged", "other", fixture.Arguments, default));
        fixture.Service.Cancel(fixture.Second.Session, queued);
        var result = await fixture.WaitCompletedAsync(fixture.Second, queued);
        Assert.Equal("Cancelled", result.GetProperty("state").GetString());
        fixture.Executor.ReleaseFirst.TrySetResult();
        Assert.Equal(1, fixture.Executor.Started);
    }

    [Fact]
    public async Task ExpiredWorkIsNeverRevivedByQueuePolling()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.StartAsync(fixture.First);
        await fixture.Executor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ReadAsync(fixture.First.Session, id, 0, default));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan delta) => now += delta;
    }

    private sealed class ControlledExecutor : IPlatformLlmJobExecutor
    {
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? LastRequestJson;
        public int Started;
        private int active;
        public int MaximumActive;
        public async IAsyncEnumerable<CapabilityResult> ExecuteAsync(AgentSession session, RequestCapability request,
            [EnumeratorCancellation] CancellationToken token)
        {
            LastRequestJson = request.Payload.ToStringUtf8();
            var count = Interlocked.Increment(ref Started);
            MaximumActive = Math.Max(MaximumActive, Interlocked.Increment(ref active));
            try
            {
                if (count == 1) { FirstStarted.TrySetResult(); await ReleaseFirst.Task.WaitAsync(token); }
                else SecondStarted.TrySetResult();
                yield return new() { RequestId = request.RequestId, Succeeded = true, Sequence = 0,
                    Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new { text = "Done" })) };
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }

    private sealed record TestWork(AgentSession Session, Guid WorkId)
    {
        public Guid Work => WorkId;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required ServiceProvider Services { get; init; }
        public required PlatformLlmJobService Service { get; init; }
        public required ManualClock Clock { get; init; }
        public required ControlledExecutor Executor { get; init; }
        public required TestWork First { get; init; }
        public required TestWork Second { get; init; }
        public JsonElement Arguments { get; } = JsonSerializer.SerializeToElement(new { providerProfileId = Guid.NewGuid(), messages = new[] { new { role = "user", text = "Hi" } } });

        public static async Task<Fixture> CreateAsync(PlatformLlmJobOptions? options = null)
        {
            var clock = new ManualClock();
            var executor = new ControlledExecutor();
            var database = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddDbContext<CSweetDbContext>(o => o.UseInMemoryDatabase(database));
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddScoped<AgentWorkInbox>();
            services.AddSingleton<IPlatformLlmJobExecutor>(executor);
            services.AddOptions<AgentRuntimeManagerOptions>();
            var provider = services.BuildServiceProvider();
            var queue = new PlatformLlmJobService(provider.GetRequiredService<IServiceScopeFactory>(), options ?? new(), clock,
                NullLogger<PlatformLlmJobService>.Instance);
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var org = Guid.NewGuid().ToString();
            TestWork Seed()
            {
                var installation = Guid.NewGuid(); var runtime = Guid.NewGuid(); var work = Guid.NewGuid();
                db.AgentInstallations.Add(new() { Id = installation, IsEnabled = true, BusinessId = org,
                    Grant = new() { AgentInstallationId = installation, GrantRevision = 1 } });
                var instance = new AgentRuntimeInstance { Id = runtime, AgentInstallationId = installation,
                    RuntimeDeadlineAt = clock.GetUtcNow().AddMinutes(2) };
                instance.TransitionTo(AgentRuntimeStatus.Starting, clock.GetUtcNow());
                instance.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, clock.GetUtcNow());
                instance.TransitionTo(AgentRuntimeStatus.Running, clock.GetUtcNow());
                db.AgentRuntimeInstances.Add(instance);
                db.AgentWorkItems.Add(new() { Id = work, AgentInstallationId = installation, OrganizationId = org,
                    Status = AgentWorkStatus.Leased, DeadlineAt = clock.GetUtcNow().AddMinutes(2),
                    Attempts = [new() { Id = Guid.NewGuid(), AgentWorkItemId = work, RuntimeInstanceId = runtime, Attempt = 1,
                        LeaseExpiresAt = clock.GetUtcNow().AddHours(1), LeaseTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("lease"))) }] });
                return new(new(Guid.NewGuid().ToString(), "test.agent", installation.ToString(), org, runtime.ToString(),
                    Guid.NewGuid().ToString(), new(new HashSet<string>(), new HashSet<string>(),
                        new HashSet<string> { PlatformCapabilities.LlmChatStream }, 1)), work);
            }
            var first = Seed(); var second = Seed();
            await db.SaveChangesAsync();
            await ((Microsoft.Extensions.Hosting.IHostedService)queue).StartAsync(default);
            return new() { Services = provider, Service = queue, Clock = clock, Executor = executor, First = first, Second = second };
        }
        public Task<Guid> StartAsync(TestWork work) => Service.StartAsync(work.Session, work.Work, 1, "lease", "request", Arguments, default);
        public async Task<JsonElement> ReadAsync(TestWork work, Guid id) => JsonSerializer.SerializeToElement(await Service.ReadAsync(work.Session, id, 0, default));
        public async Task<JsonElement> WaitCompletedAsync(TestWork work, Guid id)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (true)
            {
                var result = await ReadAsync(work, id);
                if (result.GetProperty("completed").GetBoolean()) return result;
                await Task.Delay(5, timeout.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            Executor.ReleaseFirst.TrySetResult();
            await Service.StopAsync(default);
            Service.Dispose();
            await Services.DisposeAsync();
        }
    }
}
