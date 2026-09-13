using System.Diagnostics;
using System.Text;

namespace CSweet.Compute.HyperV;

internal interface IHyperVCommandRunner
{
    Task<string> RunAsync(string script, IReadOnlyDictionary<string, string> parameters, CancellationToken token);
}

internal sealed class HyperVCommandRunner : IHyperVCommandRunner
{
    public async Task<string> RunAsync(string script, IReadOnlyDictionary<string, string> parameters, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Hyper-V compute requires Windows.");
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.WorkingDirectory = Path.GetDirectoryName(start.FileName)!;
        start.Environment["PSModulePath"] = Path.Combine(start.WorkingDirectory, "Modules");
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" + script)) }) start.ArgumentList.Add(argument);
        foreach (var parameter in parameters) start.Environment[parameter.Key] = parameter.Value;
        using var process = Process.Start(start) ?? throw new IOException("The Hyper-V helper could not be started.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var output = DrainAsync(process.StandardOutput);
        var error = DrainAsync(process.StandardError);
        try
        {
            try { await process.WaitForExitAsync(cancellation.Token); }
            catch (OperationCanceledException)
            {
                // Do not release the caller's physical-operation lock while the helper remains alive.
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0) throw new IOException("The Hyper-V operation failed; reconcile its physical outcome.");
            return (await output).Trim();
        }
        finally { await Task.WhenAll(output, error); }
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        const int limit = 65536;
        var builder = new StringBuilder(); var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
            if (builder.Length < limit) builder.Append(buffer, 0, Math.Min(count, limit - builder.Length));
        // Continue draining after the storage limit so a noisy helper cannot deadlock its pipe.
        return builder.ToString();
    }
}
