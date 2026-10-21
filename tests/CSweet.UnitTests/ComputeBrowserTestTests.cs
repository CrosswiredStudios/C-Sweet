using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Application.Compute;
using CSweet.Domain.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeBrowserTestTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Browser_submission_requires_current_authority_and_a_prepared_template(bool prepared)
    {
        await using var fixture = new ComputeBrokerTests.Fixture(); await fixture.SeedAsync();
        if (prepared) fixture.Templates.Features = new HashSet<string> { ComputeBrowserTest.Feature };
        var environment = await ComputeWorkloadTests.ReadyAsync(fixture);
        await ComputeWorkloadTests.GrantAsync(fixture, InfrastructureActions.Execute);
        var browser = new ComputeBrowserTest(Guid.NewGuid(), new('a', 40), new('b', 64), [375, 1280]);
        var request = new RequestComputeWorkload(environment.Id, 1, "browser-test", new(BrowserTest: browser));
        if (!prepared)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Broker.SubmitWorkloadAsync(fixture.Organization, fixture.Installation, request, default));
            return;
        }
        var accepted = await fixture.Broker.SubmitWorkloadAsync(fixture.Organization, fixture.Installation, request, default);
        Assert.Equal(accepted.Id, (await fixture.Broker.SubmitWorkloadAsync(fixture.Organization, fixture.Installation, request, default)).Id);
        Assert.Equal(2, accepted.Generation);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Browser_containers_are_removed_even_when_execution_is_cancelled(bool cancel)
    {
        var browser = new ComputeBrowserTest(Guid.NewGuid(), new('a', 40), new('b', 64), [375, 1280]);
        var calls = new List<ComputeGuestCommand>();
        async Task<ComputeGuestWireResult> Execute(ComputeGuestCommand command, CancellationToken token)
        {
            await Task.Yield(); calls.Add(command);
            if (calls.Count == 1 && cancel) throw new OperationCanceledException();
            return new(1, Guid.NewGuid(), command.RequestId, 0, false, [], [], false);
        }
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => ComputeBrowserTestExecutor.ExecuteAsync(browser, Execute, default));
        else await ComputeBrowserTestExecutor.ExecuteAsync(browser, Execute, default);
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { "rm", "--force", browser.ContainerName }, calls[1].Arguments);
    }

    [Fact]
    public void Browser_workload_roundtrips_and_uses_only_the_pinned_guest_runner()
    {
        var widths = new[] { 375, 1280 };
        var browser = new ComputeBrowserTest(Guid.NewGuid(), new('a', 40), new('b', 64), widths);
        var workload = new ComputeWorkload(BrowserTest: browser).Validate("linux");
        widths[0] = 800;
        Assert.Equal(375, workload.BrowserTest!.ViewportWidths[0]);
        var restored = JsonSerializer.Deserialize<ComputeWorkload>(JsonSerializer.Serialize(workload, ComputeProtocol.Json), ComputeProtocol.Json)!.Validate("linux");
        Assert.Equal(browser.RequestId, restored.EffectiveCommand!.RequestId);
        Assert.Equal("/usr/bin/docker", restored.EffectiveCommand.Executable);
        Assert.Contains("--network=none", restored.EffectiveCommand.Arguments);
        Assert.Contains("--read-only", restored.EffectiveCommand.Arguments);
        Assert.Contains(ComputeBrowserTest.Image, restored.EffectiveCommand.Arguments);
        Assert.Equal(new[] { InfrastructureActions.Execute }, restored.RequiredActions);
        Assert.Equal(64, (browser with { SourceCommitSha = new('a', 64) }).Validate().SourceCommitSha.Length);
        Assert.Throws<ArgumentException>(() => workload.Validate("windows"));
        Assert.Throws<ArgumentException>(() => (workload with { Command = workload.EffectiveCommand }).Validate("linux"));
        Assert.Throws<ArgumentException>(() => (browser with { BuildSha256 = "../../outside" }).Validate());
        Assert.Throws<ArgumentException>(() => (browser with { ViewportWidths = [375, 375] }).Validate());
    }
}
