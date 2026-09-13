using CSweet.Compute.Runtime;

namespace CSweet.UnitTests;

public sealed class ComputeGuestResponderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guest_reports_actual_platform_and_runtime_readiness(bool acceptingWork)
    {
        using var input = new MemoryStream(); using var output = new MemoryStream();
        var request = new ComputeGuestReadinessRequest(1, Guid.NewGuid());
        await ComputeGuestReadiness.WriteAsync(input, request, default); input.Position = 0;
        var calls = 0;
        await ComputeGuestReadiness.RespondAsync(input, output, _ => { calls++; return Task.FromResult(acceptingWork); }, default);
        output.Position = 0;
        var response = await ComputeGuestReadiness.ReadAsync<ComputeGuestReadinessResponse>(output, default);
        Assert.Equal(request.Challenge, response.Challenge); Assert.Equal(1, response.Version);
        Assert.Equal(OperatingSystem.IsWindows() ? "windows" : "linux", response.OperatingSystem);
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), response.Architecture);
        Assert.Equal(acceptingWork, response.AcceptingWork); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalid_request_never_queries_runtime_or_emits_readiness(bool wrongVersion)
    {
        using var input = new MemoryStream(); using var output = new MemoryStream();
        await ComputeGuestReadiness.WriteAsync(input, new ComputeGuestReadinessRequest(wrongVersion ? 2 : 1,
            wrongVersion ? Guid.NewGuid() : Guid.Empty), default); input.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ComputeGuestReadiness.RespondAsync(input, output,
            _ => throw new Exception("Must not inspect runtime"), default));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task Runtime_failure_or_shutdown_never_emits_a_ready_response()
    {
        using var input = new MemoryStream(); using var output = new MemoryStream();
        await ComputeGuestReadiness.WriteAsync(input, new ComputeGuestReadinessRequest(1, Guid.NewGuid()), default); input.Position = 0;
        await Assert.ThrowsAsync<IOException>(() => ComputeGuestReadiness.RespondAsync(input, output,
            _ => throw new IOException("Guest runtime unavailable"), default));
        Assert.Equal(0, output.Length);
        using var stop = new CancellationTokenSource(); stop.Cancel(); input.Position = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ComputeGuestReadiness.RespondAsync(input, output,
            _ => Task.FromResult(true), stop.Token));
        Assert.Equal(0, output.Length);
    }
}
