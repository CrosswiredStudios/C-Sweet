using System.Diagnostics;
using CSweet.Compute.Contracts;

namespace CSweet.Compute.Guest;

/// <summary>Guest-only systemd transient service. Requires systemd 254+; no shell or Docker dependency.</summary>
public sealed class LinuxComputeGuestProcess : IComputeGuestProcess
{
    private readonly ComputeGuestCommand command;
    private readonly string temporaryDirectory;
    private readonly string unit = "csweet-command-" + Guid.NewGuid().ToString("N") + ".service";
    private Process? process;
    private bool stopped;
    public Stream StandardOutput => process?.StandardOutput.BaseStream ?? Stream.Null;
    public Stream StandardError => process?.StandardError.BaseStream ?? Stream.Null;

    public LinuxComputeGuestProcess(ComputeGuestCommand command, string temporaryDirectory)
    {
        this.command = command.ValidateAndSnapshot("linux");
        (command with { WorkingDirectory = temporaryDirectory }).ValidateAndSnapshot("linux");
        this.temporaryDirectory = temporaryDirectory;
    }

    public Task StartAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        token.ThrowIfCancellationRequested();
        if (process is not null || stopped) throw new InvalidOperationException("Guest process adapter is single-use.");
        if (!Directory.Exists("/run/systemd/system") || !File.Exists("/usr/bin/systemd-run"))
            throw new IOException("The Linux guest requires systemd.");
        var start = StartInfo("/usr/bin/systemd-run");
        foreach (var value in new[] { "--quiet", "--wait", "--pipe", "--collect", "--service-type=exec",
            "--expand-environment=no", "--unit=" + unit, "--description=C-Sweet guest command",
            "--working-directory=" + command.WorkingDirectory, "--property=KillMode=control-group",
            "--property=KillSignal=SIGKILL", "--property=TimeoutStopSec=3s",
            "--property=RuntimeMaxSec=" + (command.TimeoutSeconds + 5) + "s",
            "--", "/usr/bin/env", "-i", "PATH=/usr/bin:/bin", "HOME=" + command.WorkingDirectory,
            "TMPDIR=" + temporaryDirectory, command.Executable }.Concat(command.Arguments)) start.ArgumentList.Add(value);
        process = Process.Start(start) ?? throw new IOException("Linux guest command did not start.");
        process.StandardInput.Close();
        return Task.CompletedTask;
    }

    public async Task<int> WaitForExitAsync(CancellationToken token)
    {
        if (process is null) throw new InvalidOperationException("Guest process has not started.");
        await process.WaitForExitAsync(token); return process.ExitCode;
    }

    public async Task StopAsync(CancellationToken token)
    {
        if (stopped) return;
        if (process is null) { stopped = true; return; }
        var alreadyExited = process.HasExited;
        try
        {
            var stop = await ControlAsync(["stop", unit], token);
            var state = await ControlAsync(["show", "--property=LoadState", "--property=ActiveState", unit], token);
            var missing = state.Output.Split('\n').Contains("LoadState=not-found");
            var inactive = state.Output.Split('\n').Any(line => line is "ActiveState=inactive" or "ActiveState=failed");
            // A missing unit while the launch client was still running can be an unconfirmed admission race.
            if (!(missing && alreadyExited) && !(stop.ExitCode == 0 && (inactive || missing)))
                throw new IOException("Linux guest service cleanup could not be confirmed.");
            if (!process.HasExited) await process.WaitForExitAsync(token);
            stopped = true;
        }
        finally
        {
            // Stop the CLI as well. RuntimeMaxSec remains an independent guest-service backstop.
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(token); }
            // The coordinator still owns the output streams; do not dispose Process before it drains them.
        }
    }

    public void Dispose() => process?.Dispose();
    private static ProcessStartInfo StartInfo(string executable)
    {
        var result = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        result.Environment.Clear(); result.Environment["PATH"] = "/usr/bin:/bin";
        result.Environment["LC_ALL"] = "C";
        return result;
    }

    private static async Task<(int ExitCode, string Output)> ControlAsync(string[] arguments, CancellationToken token)
    {
        var start = StartInfo("/usr/bin/systemctl");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var control = Process.Start(start) ?? throw new IOException("Guest service control failed.");
        control.StandardInput.Close();
        async Task<string> DrainAsync(StreamReader reader)
        {
            var result = new System.Text.StringBuilder(); var buffer = new char[1024];
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), token); if (count == 0) return result.ToString();
                if (result.Length + count > 4096) throw new IOException("Guest service control response exceeds its limit.");
                result.Append(buffer, 0, count);
            }
        }
        var output = DrainAsync(control.StandardOutput); var error = DrainAsync(control.StandardError);
        try { await control.WaitForExitAsync(token); await Task.WhenAll(output, error); return (control.ExitCode, await output); }
        finally
        {
            if (!control.HasExited) control.Kill(entireProcessTree: true);
            try { await Task.WhenAll(output, error); } catch (Exception failure) when (failure is IOException or OperationCanceledException) { }
        }
    }
}
