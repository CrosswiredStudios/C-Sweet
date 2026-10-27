using System.Diagnostics;

namespace CSweet.UnitTests;

public sealed class ComputeRuntimeUpdateScriptTests
{
    [Fact]
    public async Task Normal_provider_update_preserves_durable_state_and_rolls_back_failed_validation()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"scripts","Update-ComputeProviderRuntime.ps1")))root=root.Parent;
        Assert.NotNull(root);
        var shell=OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")
            : "pwsh";
        var start=new ProcessStartInfo(shell){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-File");
        start.ArgumentList.Add(Path.Combine(root!.FullName,"tests","compute-runtime-update.tests.ps1"));
        start.ArgumentList.Add("-RepositoryRoot");start.ArgumentList.Add(root.FullName);
        using var process=Process.Start(start)!;using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output=process.StandardOutput.ReadToEndAsync(timeout.Token);var error=process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);var stdout=await output;var stderr=await error;
        Assert.True(process.ExitCode==0,stdout+stderr);Assert.Contains("preserved",stdout);
    }
}
