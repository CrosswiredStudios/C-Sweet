using System.Net;

namespace CSweet.UI.Pages;

public partial class WorkBoards
{
    private readonly SemaphoreSlim _boardReadLock = new(1, 1);
    private CancellationTokenSource _boardReadCancellation = new();
    private bool _disposed;

    // Retry reads only. Mutations keep their existing revision/idempotency handling.
    private async Task<HttpResponseMessage> GetBoardReadAsync(string uri, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await Http.GetAsync(uri, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    response.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (attempt == 3 || !IsTransientBoardStatus(response.StatusCode)) return response;
                response.Dispose();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < 3) { }
            catch (HttpRequestException exception) when (attempt < 3 &&
                (exception.StatusCode is null || IsTransientBoardStatus(exception.StatusCode.Value))) { }

            await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
        }
    }

    private static bool IsTransientBoardStatus(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout || (int)status >= 500;

    private async Task RunBoardReadAsync(Func<CancellationToken, Task> read)
    {
        if (_disposed) return;
        var cancellationToken = _boardReadCancellation.Token;
        try
        {
            await _boardReadLock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await read(cancellationToken);
            }
            finally { _boardReadLock.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
