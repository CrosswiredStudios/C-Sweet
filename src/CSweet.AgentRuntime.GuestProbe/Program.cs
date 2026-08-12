using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
{
    ["linux-guest"] = OperatingSystem.IsLinux(),
    ["artifact-root"] = Path.GetFullPath(Environment.CurrentDirectory)
        .StartsWith("/run/csweet/artifact/payload", StringComparison.Ordinal),
    ["no-network-interface"] = NetworkInterfaces().All(name => name == "lo"),
    ["no-default-route"] = !HasDefaultRoute(),
    ["no-host-filesystem-mount"] = !HasForbiddenMount(),
    ["ephemeral-scratch-mounted"] = IsScratchMounted(),
    ["outbound-network-denied"] = await OutboundNetworkDeniedAsync(),
    ["broker-socket-present"] = File.Exists(
        Environment.GetEnvironmentVariable("CSweet__Agent__McpUnixSocketPath") ?? string.Empty),
    ["workload-unprivileged"] = !OperatingSystem.IsLinux() || NativeMethods.GetEffectiveUserId() != 0,
    ["builder-executable-present"] = File.Exists("/usr/lib/csweet/builder/CSweet.AgentRuntime.Builder"),
    ["dotnet-sdk-present"] = await DotNetSdkPresentAsync()
};
var report = new GuestProbeReport(
    "csweet-hardware-vm-smoke-v14",
    checks.All(check => check.Value),
    checks,
    Environment.OSVersion.VersionString,
    DateTimeOffset.UtcNow);
var body = JsonSerializer.SerializeToUtf8Bytes(report);
await PostReportAsync(body);
return report.Passed ? 0 : 2;

static IReadOnlyList<string> NetworkInterfaces()
{
    try
    {
        return Directory.Exists("/sys/class/net")
            ? Directory.EnumerateDirectories("/sys/class/net").Select(Path.GetFileName).Where(name => name is not null).Cast<string>().ToArray()
            : ["missing-sysfs"];
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        return ["inspection-failed"];
    }
}

static bool HasDefaultRoute()
{
    try
    {
        if (!File.Exists("/proc/net/route")) return true;
        return File.ReadLines("/proc/net/route").Skip(1).Any(line =>
        {
            var fields = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 1 && fields[1] == "00000000";
        });
    }
    catch (IOException) { return true; }
}

static bool HasForbiddenMount()
{
    string[] forbidden = ["9p", "cifs", "smb3", "nfs", "nfs4", "vboxsf", "hgfs", "drvfs", "fuse.sshfs"];
    try
    {
        return !File.Exists("/proc/self/mountinfo") || File.ReadLines("/proc/self/mountinfo").Any(line =>
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0) return true;
            var fields = line[(separator + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length == 0 || forbidden.Contains(fields[0], StringComparer.Ordinal);
        });
    }
    catch (IOException) { return true; }
}

static bool IsScratchMounted()
{
    try
    {
        return File.Exists("/proc/self/mountinfo") && File.ReadLines("/proc/self/mountinfo").Any(line =>
            line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(4).FirstOrDefault() == "/run/csweet");
    }
    catch (IOException) { return false; }
}

static async Task<bool> OutboundNetworkDeniedAsync()
{
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try
    {
        await socket.ConnectAsync("1.1.1.1", 443, timeout.Token);
        return false;
    }
    catch (Exception exception) when (exception is SocketException or OperationCanceledException)
    {
        return true;
    }
}

static async Task<bool> DotNetSdkPresentAsync()
{
    if (!File.Exists("/usr/bin/dotnet")) return false;
    try
    {
        var start = new ProcessStartInfo("/usr/bin/dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--list-sdks");
        using var process = Process.Start(start);
        if (process is null) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask;
        await errorTask;
        return process.ExitCode == 0 && output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.TrimStart().StartsWith("10.", StringComparison.Ordinal));
    }
    catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
    {
        return false;
    }
}

static async Task PostReportAsync(byte[] body)
{
    var path = Environment.GetEnvironmentVariable("CSweet__Agent__McpUnixSocketPath") ??
        throw new InvalidOperationException("The certification broker socket is missing.");
    using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
    await using var stream = new NetworkStream(socket, ownsSocket: false);
    var header = Encoding.ASCII.GetBytes(
        $"POST /mcp HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(header);
    await stream.WriteAsync(body);
    await stream.FlushAsync();
    using var response = new MemoryStream();
    await stream.CopyToAsync(response);
    var status = Encoding.ASCII.GetString(response.GetBuffer(), 0, Math.Min(64, checked((int)response.Length)));
    if (!status.StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal))
        throw new IOException("The certification report was rejected by the host broker.");
}

internal sealed record GuestProbeReport(
    string Suite,
    bool Passed,
    IReadOnlyDictionary<string, bool> Checks,
    string GuestOperatingSystem,
    DateTimeOffset CompletedAt);

internal static class NativeMethods
{
    [DllImport("libc", EntryPoint = "geteuid")]
    internal static extern uint GetEffectiveUserId();
}
