using System.Net;
using CSweet.ConnectionService.Protocol;

namespace CSweet.UI.Setup;

public static class OwnerFailureDisplay
{
    public const string StatusHeader = "X-CSweet-Owner-Status";
    public static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var message = "Operation unavailable. Check local bootstrap and current permissions. A submitted mutation may have succeeded; check service state before retrying.";
        if (response.Headers.TryGetValues(StatusHeader, out var values))
        {
            using var iterator = values.GetEnumerator();
            var value = iterator.MoveNext() ? iterator.Current : "";
            if (!iterator.MoveNext() && value.Length == 3 && int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var status) && status is >= 400 and <= 599 &&
                OwnerFailureDiagnostic.Read(response.Headers, (HttpStatusCode)status) is { } hint)
                message += $" Diagnostic: HTTP {status}; stage {hint.Stage}; code {hint.Code}; type {hint.ExceptionType}; correlation {hint.CorrelationId}; elapsed {hint.ElapsedMilliseconds} ms. Diagnostic hints do not establish retry safety.";
        }
        throw new OwnerPanelOperationException(message);
    }
}

public sealed class OwnerPanelOperationException : Exception
{
    internal OwnerPanelOperationException(string safeMessage) : base(safeMessage) { }
}
