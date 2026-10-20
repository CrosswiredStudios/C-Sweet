using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;

namespace CSweet.Compute.HyperV;

internal static class ComputeLocalRetirement
{
    internal static async Task ScheduleAsync(ComputeProviderConfiguration configuration,
        ComputeProviderRetirement request, CancellationToken token)
    {
        if (request.BusinessId != configuration.Enrollment.OrganizationId || request.NodeId != configuration.Enrollment.NodeId ||
            request.CleanupId == Guid.Empty || request.ReceiptToken is not { Length: 64 } || request.ReceiptToken.Any(x => !Uri.IsHexDigit(x)))
            throw new UnauthorizedAccessException("Retirement authority does not match the installed provider.");
        var installation = new ComputeLocalInstallation(request.BusinessId);
        var root = installation.Root;
        var legacy = new ComputeLocalInstallation(null);
        var shared = string.Equals(Path.GetFullPath(configuration.WorkloadDirectory), Path.Combine(legacy.Root, "workloads"), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Path.Combine(legacy.Root, "runtime"), StringComparison.OrdinalIgnoreCase) && !Directory.Exists(root);
        if (!shared && (!string.Equals(Path.GetFullPath(configuration.WorkloadDirectory), Path.Combine(root, "workloads"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Path.Combine(root, "runtime"), StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Only an installed business-scoped provider can retire business storage.");
        WindowsComputeProtectedPaths.Verify(shared ? legacy.Root : root);
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CSweet", "ComputeRetirement");
        var directory = Path.Combine(parent, request.BusinessId.ToString("N"));
        await new HyperVCommandRunner().RunAsync("""
            $acl = New-Object System.Security.AccessControl.DirectorySecurity
            $acl.SetAccessRuleProtection($true, $false)
            $acl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')))
            foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
                $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
                    (New-Object System.Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
                $acl.AddAccessRule($rule)
            }
            foreach ($path in @($env:CSWEET_RETIRE_PARENT, $env:CSWEET_RETIRE_DIRECTORY)) {
                if (Test-Path -LiteralPath $path) {
                    if (((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked retirement directory.' }
                } else { $null = New-Item -ItemType Directory -Path $path }
                Set-Acl -LiteralPath $path -AclObject $acl
            }
            """, new Dictionary<string, string> { ["CSWEET_RETIRE_PARENT"] = parent, ["CSWEET_RETIRE_DIRECTORY"] = directory }, token);
        var requestPath = Path.Combine(directory, "request.json");
        var scriptPath = Path.Combine(directory, "retire.ps1");
        WindowsComputeProtectedPaths.Verify(directory);
        await using (var source = typeof(ComputeLocalRetirement).Assembly.GetManifestResourceStream("CSweet.Compute.HyperV.Retire-ComputeBusiness.ps1")!)
        await using (var destination = new FileStream(scriptPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            await source.CopyToAsync(destination, token);
        var json = JsonSerializer.Serialize(new
        {
            request.CleanupId, request.BusinessId, request.NodeId, request.ReceiptToken,
            configuration.CoreOrigin, configuration.CoreCertificateSha256, configuration.CertificateThumbprint,
            PreserveSharedProvider = shared
        }, ComputeProtocol.Json);
        var incoming = requestPath + ".incoming";
        await File.WriteAllTextAsync(incoming, json, token);
        File.Move(incoming, requestPath, overwrite: true);
        await new HyperVCommandRunner().RunAsync("""
            $name = 'CSweet.Compute.Retire.' + $env:CSWEET_RETIRE_BUSINESS
            $action = New-ScheduledTaskAction -Execute (Join-Path $PSHOME 'powershell.exe') -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $env:CSWEET_RETIRE_SCRIPT + '"')
            $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds(15) -RepetitionInterval (New-TimeSpan -Minutes 1)
            $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
            $null = Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force
            """, new Dictionary<string, string>
            { ["CSWEET_RETIRE_BUSINESS"] = request.BusinessId.ToString("N"), ["CSWEET_RETIRE_SCRIPT"] = scriptPath }, token);
    }
}
