using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using CSweet.Compute.Contracts;
using Microsoft.Win32.SafeHandles;

namespace CSweet.Compute.Guest;

/// <summary>
/// Guest-only Windows process adapter. Job membership is applied atomically by CreateProcess;
/// the job is not inherited and kills its members on last-handle close. This is process lifetime
/// containment inside a VM, not a substitute for the VM security boundary or a restricted guest account.
/// </summary>
[SupportedOSPlatform("windows10.0")]
public sealed class WindowsComputeGuestProcess : IComputeGuestProcess
{
    private readonly ComputeGuestCommand command;
    private readonly string temporaryDirectory;
    private SafeFileHandle? job;
    private SafeFileHandle? process;
    private bool started;
    private bool stopped;
    private bool cleanupFailed;
    public Stream StandardOutput { get; private set; } = Stream.Null;
    public Stream StandardError { get; private set; } = Stream.Null;

    public WindowsComputeGuestProcess(ComputeGuestCommand command, string temporaryDirectory)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new PlatformNotSupportedException();
        this.command = command.ValidateAndSnapshot("windows");
        (command with { WorkingDirectory = temporaryDirectory }).ValidateAndSnapshot("windows");
        if (!Path.IsPathFullyQualified(temporaryDirectory) || !Directory.Exists(temporaryDirectory) ||
            temporaryDirectory.Contains('\0')) throw new ArgumentException("Installed guest temporary directory is invalid.");
        this.temporaryDirectory = Path.GetFullPath(temporaryDirectory);
    }

    public Task StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (started || stopped) throw new InvalidOperationException("Guest process adapter is single-use.");
        started = true;
        job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw Failure();
        var limits = new Native.ExtendedLimits { Basic = new() { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE; no breakaway
        if (!Native.SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<Native.ExtendedLimits>())) throw Failure();

        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        StandardOutput = stdout;
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        StandardError = stderr;
        using var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        // Only these three child ends may be inherited; neither read endpoints nor the job handle may be.
        if (!Native.SetHandleInformation(stdout.SafePipeHandle, 1, 0) ||
            !Native.SetHandleInformation(stderr.SafePipeHandle, 1, 0) ||
            !Native.SetHandleInformation(stdin.SafePipeHandle, 1, 0)) throw Failure();
        using var attributes = new AttributeList(job.DangerousGetHandle(),
            [stdin.ClientSafePipeHandle.DangerousGetHandle(), stdout.ClientSafePipeHandle.DangerousGetHandle(),
                stderr.ClientSafePipeHandle.DangerousGetHandle()]);
        var startup = new Native.StartupInfoEx
        {
            Info = new() { Size = Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x100,
                Input = stdin.ClientSafePipeHandle.DangerousGetHandle(), Output = stdout.ClientSafePipeHandle.DangerousGetHandle(),
                Error = stderr.ClientSafePipeHandle.DangerousGetHandle() }, Attributes = attributes.Pointer
        };
        // Values come from this installed guest. Never copy the service's ambient environment.
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windows = Directory.GetParent(system)?.FullName ?? throw Failure();
        var environment = Marshal.StringToHGlobalUni($"PATH={system}\0SystemRoot={windows}\0TEMP={temporaryDirectory}\0TMP={temporaryDirectory}\0\0");
        try
        {
            token.ThrowIfCancellationRequested();
            var line = new StringBuilder(string.Join(' ', new[] { command.Executable }.Concat(command.Arguments).Select(Quote)));
            if (line.Length >= 32767) throw new ArgumentException("Guest command line exceeds the Windows limit.");
            // Explicit application path, no shell, hidden window, Unicode environment, explicit job and handle lists.
            if (!Native.CreateProcessW(command.Executable, line, IntPtr.Zero, IntPtr.Zero, true,
                    0x08080400, environment, command.WorkingDirectory, ref startup, out var created)) throw Failure();
            process = new SafeFileHandle(created.Process, true);
            using var thread = new SafeFileHandle(created.Thread, true);
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
            stdout.DisposeLocalCopyOfClientHandle(); stderr.DisposeLocalCopyOfClientHandle(); stdin.DisposeLocalCopyOfClientHandle();
        }
        return Task.CompletedTask;
    }

    public async Task<int> WaitForExitAsync(CancellationToken token)
    {
        if (process is null || stopped) throw new InvalidOperationException("Guest process is not running.");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var status = Native.WaitForSingleObject(process, 0);
            if (status == 0) break;
            if (status != 258) throw Failure();
            await Task.Delay(20, token);
        }
        if (!Native.GetExitCodeProcess(process, out var exit)) throw Failure();
        return unchecked((int)exit);
    }

    public async Task StopAsync(CancellationToken token)
    {
        if (cleanupFailed) throw Failure();
        if (stopped) return;
        try
        {
            if (job is { IsInvalid: false, IsClosed: false })
            {
                if (!Native.TerminateJobObject(job, 1)) throw Failure();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Native.QueryInformationJobObject(job, 1, out var accounting,
                            Marshal.SizeOf<Native.Accounting>(), IntPtr.Zero)) throw Failure();
                    if (accounting.ActiveProcesses == 0) break;
                    await Task.Delay(20, token);
                }
            }
            stopped = true;
        }
        catch { cleanupFailed = true; throw; }
        finally
        {
            // Last-handle close is a second termination mechanism even when confirmation fails.
            process?.Dispose(); job?.Dispose();
            if (StandardOutput is AnonymousPipeServerStream stdout) stdout.DisposeLocalCopyOfClientHandle();
            if (StandardError is AnonymousPipeServerStream stderr) stderr.DisposeLocalCopyOfClientHandle();
        }
    }

    private static IOException Failure() => new("Windows guest process operation failed.");

    // Windows CRT argument encoding, with an explicit application path. Shell metacharacters stay literal.
    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1);
            else result.Append('\\', slashes);
            result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private sealed class AttributeList : IDisposable
    {
        public IntPtr Pointer { get; private set; }
        private IntPtr handles;
        private IntPtr jobs;
        private bool initialized;
        public AttributeList(IntPtr job, IntPtr[] inherited)
        {
            try
            {
                nuint size = 0;
                Native.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
                if (size == 0 || size > 65536) throw Failure();
                Pointer = Marshal.AllocHGlobal((int)size);
                if (!Native.InitializeProcThreadAttributeList(Pointer, 2, 0, ref size)) throw Failure();
                initialized = true;
                handles = Marshal.AllocHGlobal(inherited.Length * IntPtr.Size);
                Marshal.Copy(inherited, 0, handles, inherited.Length);
                jobs = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobs, job);
                if (!Native.UpdateProcThreadAttribute(Pointer, 0, 0x20002, handles, (nuint)(inherited.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero) ||
                    !Native.UpdateProcThreadAttribute(Pointer, 0, 0x2000D, jobs, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw Failure();
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (initialized) { Native.DeleteProcThreadAttributeList(Pointer); initialized = false; }
            Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero;
            Marshal.FreeHGlobal(handles); handles = IntPtr.Zero;
            Marshal.FreeHGlobal(jobs); jobs = IntPtr.Zero;
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public nuint MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
            public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Accounting
        {
            public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
            public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        {
            public int Size;
            public IntPtr Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
            public ushort ShowWindow, ReservedSize;
            public IntPtr ReservedPointer, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateJobObjectW(IntPtr security, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits value, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(SafeFileHandle job, int type, out Accounting value, int size, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(SafeFileHandle job, uint exit);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(SafePipeHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exit);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processSecurity,
            IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment,
            string directory, ref StartupInfoEx startup, out ProcessInformation process);
    }
}
