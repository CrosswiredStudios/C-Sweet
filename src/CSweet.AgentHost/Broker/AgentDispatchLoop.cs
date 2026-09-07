namespace CSweet.AgentHost.Broker;

internal static class AgentDispatchLoop
{
    internal static async Task RunAsync(Func<CancellationToken, Task> dispatch, TimeProvider clock,
        ILogger logger, CancellationToken token, TimeSpan? interval = null)
    {
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(2), clock);
        try
        {
            do
            {
                try { await dispatch(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    // Each iteration owns a fresh scope. Durable pending records are retried;
                    // a database restart must not take the MCP/control-plane host down.
                    logger.LogError(exception, "Agent event dispatch failed; retrying on the next iteration.");
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
