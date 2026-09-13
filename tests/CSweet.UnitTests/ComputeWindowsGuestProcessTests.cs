using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Guest;

namespace CSweet.UnitTests;

public sealed class ComputeWindowsGuestProcessTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "csweet-guest-identity-" + Guid.NewGuid().ToString("N"));
    public ComputeWindowsGuestProcessTests()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;
        Directory.CreateDirectory(root);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "guest-fixture")))
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
    }
    public void Dispose()
    {
        var full = Path.GetFullPath(root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("csweet-guest-identity-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid fixture cleanup path.");
        // Exit assertions remain strict. Windows may briefly retain an executable image mapping
        // after exit; retry only removal of this already verified fixture directory.
        for (var attempt = 0; Directory.Exists(full); attempt++)
        {
            try { Directory.Delete(full, true); break; }
            catch (Exception error) when (attempt < 20 && error is IOException or UnauthorizedAccessException)
            { Thread.Sleep(50); }
        }
    }
    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute() { if (!OperatingSystem.IsWindowsVersionAtLeast(10)) Skip = "Windows 10+ native process test."; }
    }

    [WindowsFact]
    public async Task Native_command_preserves_argument_boundaries_and_exit_status()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;
        var expected = new[] { "", "a b", "a\"b", "C:\\trailing\\", "; & $(literal)", "unicode-\u20ac" };
        var command = Command(["arguments", .. expected]);
        var runner = new ComputeGuestExecution("windows", CreateProcess);
        var result = await runner.ExecuteAsync(command, default);
        Assert.Equal(7, result.ExitCode); Assert.False(result.TimedOut);
        Assert.Equal(expected, JsonSerializer.Deserialize<string[]>(result.StandardOutput));
        Assert.True(runner.AcceptingWork);
    }

    [WindowsFact]
    public async Task Native_command_does_not_inherit_ambient_environment()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;
        var key = "CSWEET_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(key, "must-not-inherit");
        try
        {
            var runner = new ComputeGuestExecution("windows", CreateProcess);
            var result = await runner.ExecuteAsync(Command(["environment", key]), default);
            Assert.Equal(0, result.ExitCode);
            var values = JsonSerializer.Deserialize<string?[]>(result.StandardOutput)!;
            Assert.Null(values[0]); Assert.False(string.IsNullOrEmpty(values[1]));
            Assert.Equal(root, values[2]);
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    [WindowsFact]
    public async Task Native_command_drains_large_output_under_a_combined_byte_limit()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;
        var runner = new ComputeGuestExecution("windows", CreateProcess);
        var result = await runner.ExecuteAsync(Command(["output"]) with { MaximumOutputBytes = 123 }, default);
        Assert.Equal(0, result.ExitCode); Assert.True(result.Truncated);
        Assert.Equal(123, result.StandardOutput.Length + result.StandardError.Length);
    }

    [WindowsFact]
    public async Task Parent_exit_cleans_up_native_descendant_and_inherited_output_handles()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;
        var runner = new ComputeGuestExecution("windows", CreateProcess);
        var result = await runner.ExecuteAsync(Command(["spawn"]), default);
        Assert.Equal(0, result.ExitCode); Assert.False(result.TimedOut);
        var id = int.Parse(Encoding.UTF8.GetString(result.StandardOutput).Trim());
        try { using var child = Process.GetProcessById(id); Assert.True(child.HasExited); }
        catch (ArgumentException) { /* Removed from the process table. */ }
        Assert.True(runner.AcceptingWork);
    }

    [WindowsFact]
    public async Task Native_command_timeout_confirms_cleanup_and_allows_next_command()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;
        var runner = new ComputeGuestExecution("windows", CreateProcess);
        var result = await runner.ExecuteAsync(Command(["hold"]) with { TimeoutSeconds = 1 }, default);
        Assert.True(result.TimedOut); Assert.Null(result.ExitCode); Assert.True(runner.AcceptingWork);
        Assert.Equal(7, (await runner.ExecuteAsync(Command(["arguments"]), default)).ExitCode);
    }

    [WindowsFact]
    public async Task Failed_native_start_is_cleaned_up_without_running_an_alternative()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;
        var runner = new ComputeGuestExecution("windows", CreateProcess);
        await Assert.ThrowsAsync<IOException>(() => runner.ExecuteAsync(Command([]) with
            { Executable = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".exe") }, default));
        Assert.True(runner.AcceptingWork);
    }

    [WindowsFact]
    public async Task Native_stdin_is_closed_and_caller_cancellation_cleans_up()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;
        var runner = new ComputeGuestExecution("windows", CreateProcess);
        var input = await runner.ExecuteAsync(Command(["stdin"]), default);
        Assert.Equal(0, input.ExitCode); Assert.Equal("0", Encoding.UTF8.GetString(input.StandardOutput));
        using var cancelled = new CancellationTokenSource();
        var pending = runner.ExecuteAsync(Command(["hold"]), cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(runner.AcceptingWork);
    }

    private IComputeGuestProcess CreateProcess(ComputeGuestCommand value)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new PlatformNotSupportedException();
        return new WindowsComputeGuestProcess(value, root);
    }
    private ComputeGuestCommand Command(string[] arguments) => new(Guid.NewGuid(),
        Path.Combine(root, "CSweet.Compute.GuestFixture.exe"),
        root, arguments, 10, 65536);
}
