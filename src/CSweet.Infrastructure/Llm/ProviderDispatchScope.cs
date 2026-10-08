namespace CSweet.Infrastructure.Llm;

public sealed class ProviderDispatchDeniedException() : InvalidOperationException("The provider request is no longer authorized.");

/// <summary>Trusted in-process guards, never populated from an agent request or prompt.</summary>
public sealed class ProviderDispatchScope : IDisposable
{
    private static readonly AsyncLocal<ProviderDispatchScope?> Current = new();
    private readonly ProviderDispatchScope? previous;
    private readonly Func<CancellationToken, Task>? authorize;
    private readonly Action? dispatched;
    private readonly Action? denied;
    private bool disposed;

    public ProviderDispatchScope(Func<CancellationToken, Task>? authorize, Action? dispatched = null, Action? denied = null)
    {
        this.authorize = authorize; this.dispatched = dispatched; this.denied = denied;
        previous = Current.Value; Current.Value = this;
    }

    public static async Task AuthorizeCurrentAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        for (var scope = Current.Value; scope is not null; scope = scope.previous)
        {
            if (scope.disposed) throw new ProviderDispatchDeniedException();
            if (scope.authorize is not null) await scope.authorize(token);
        }
    }

    internal static void RecordDispatch()
    {
        for (var scope = Current.Value; scope is not null; scope = scope.previous) scope.dispatched?.Invoke();
    }

    internal static void RecordDenial()
    {
        for (var scope = Current.Value; scope is not null; scope = scope.previous) scope.denied?.Invoke();
    }

    public void Dispose() { disposed = true; Current.Value = previous; }
}

/// <summary>Marks clients whose transport invokes the dispatch scope on every HTTP attempt.</summary>
public interface IProviderDispatchTransport { }
