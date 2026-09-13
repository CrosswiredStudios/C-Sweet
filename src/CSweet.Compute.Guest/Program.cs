using CSweet.Compute.Guest;

if (args.Length != 1 || args[0] != "--serve-hyperv")
{
    Console.Error.WriteLine("Usage inside a Linux or Windows Hyper-V guest: CSweet.Compute.Guest --serve-hyperv");
    return 2;
}
using var shutdown = new CancellationTokenSource();
using var termination = OperatingSystem.IsLinux() ? System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, signal => { signal.Cancel = true; shutdown.Cancel(); }) : null;
Console.CancelKeyPress += (_, value) => { value.Cancel = true; shutdown.Cancel(); };
try
{
    if (OperatingSystem.IsLinux()) await LinuxComputeGuestServer.RunAsync(shutdown.Token);
    else await WindowsComputeGuestServer.RunAsync(shutdown.Token);
    return 0;
}
catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or PlatformNotSupportedException)
{ Console.Error.WriteLine("compute-guest-startup-failed"); return 1; }
