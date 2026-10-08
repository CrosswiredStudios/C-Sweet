using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CSweet.Memory.Evaluate;

// Process-local evaluation instrumentation. No query text, parameters or identifiers are retained.
internal sealed class DatabaseCounter : IDisposable
{
    private long count;
    private readonly ActivityListener? listener;
    private readonly AutoExtension? extension;
    private readonly Trace? trace;

    public string Unit { get; }
    public long Count => Interlocked.Read(ref count);

    public DatabaseCounter(string provider)
    {
        if (provider == "postgres")
        {
            Unit = "Npgsql command activities (batches count as one command)";
            listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
                {
                    if (activity.Kind == ActivityKind.Client) Interlocked.Increment(ref count);
                }
            };
            ActivitySource.AddActivityListener(listener);
        }
        else
        {
            Unit = "SQLite prepared statement executions (including connection PRAGMA statements)";
            SQLitePCL.Batteries_V2.Init();
            trace = (_, _, _, _) => { Interlocked.Increment(ref count); return 0; };
            var tracePointer = Marshal.GetFunctionPointerForDelegate(trace);
            extension = (database, _, _) => TraceV2(database, 1, tracePointer, IntPtr.Zero);
            if (AutoExtensionNative(Marshal.GetFunctionPointerForDelegate(extension)) != 0)
                throw new InvalidOperationException("SQLite command instrumentation could not be installed.");
        }
    }

    public void Dispose()
    {
        listener?.Dispose();
        if (extension is not null) CancelAutoExtension(Marshal.GetFunctionPointerForDelegate(extension));
        // All evaluator store connections are closed before disposal.
        GC.KeepAlive(trace);
        GC.KeepAlive(extension);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AutoExtension(IntPtr database, IntPtr error, IntPtr api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Trace(uint mask, IntPtr context, IntPtr statement, IntPtr sql);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_auto_extension", CallingConvention = CallingConvention.Cdecl)]
    private static extern int AutoExtensionNative(IntPtr callback);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_cancel_auto_extension", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CancelAutoExtension(IntPtr callback);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_trace_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int TraceV2(IntPtr database, uint mask, IntPtr callback, IntPtr context);
}
