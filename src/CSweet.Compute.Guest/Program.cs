using CSweet.Compute.Guest;

if (args.Length != 1 || args[0] is not ("--serve-hyperv" or "--serve-virtio"))
{
    Console.Error.WriteLine("Usage inside a Compute VM: CSweet.Compute.Guest --serve-hyperv or --serve-virtio (Linux)");
    return 2;
}
using var shutdown = new CancellationTokenSource();
using var termination = OperatingSystem.IsLinux() ? System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, signal => { signal.Cancel = true; shutdown.Cancel(); }) : null;
Console.CancelKeyPress += (_, value) => { value.Cancel = true; shutdown.Cancel(); };
try
{
    if (OperatingSystem.IsLinux()) await LinuxComputeGuestServer.RunAsync(shutdown.Token, virtio: args[0] == "--serve-virtio");
    else if (args[0] == "--serve-virtio") return 2;
    else await WindowsComputeGuestServer.RunAsync(shutdown.Token);
    return 0;
}
catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or PlatformNotSupportedException)
{ Console.Error.WriteLine("compute-guest-startup-failed"); return 1; }
