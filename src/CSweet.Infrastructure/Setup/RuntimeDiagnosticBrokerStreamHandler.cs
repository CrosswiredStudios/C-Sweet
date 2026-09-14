using System.Text;
using CSweet.AgentBroker;

namespace CSweet.Infrastructure.Setup;

internal sealed class RuntimeDiagnosticBrokerStreamHandler(
    Guid workloadId,
    Guid installationId) : IGuestBrokerStreamHandler
{
    private const int MaximumDiagnosticBytes = 16 * 1024;
    private const int MaximumDiagnosticCharacters = 8 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private long _nextSequence;
    private string? _lastFailure;

    public string? Latest { get; private set; }

    // Builder guests may only send diagnostics in GuestExit. Even streaming guests can
    // exit before their first periodic chunk, so preserve the final bounded tail as well.
    public void CaptureExitDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return;
        Capture(detail);
    }

    public Task HandleAsync(GuestBrokerStreamContext chunk, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (chunk.WorkloadId != workloadId || chunk.InstallationId != installationId ||
            !string.Equals(chunk.StreamId, "runtime.logs", StringComparison.Ordinal) ||
            chunk.Sequence != _nextSequence || chunk.Content.Length > MaximumDiagnosticBytes ||
            chunk.Completed || !string.IsNullOrEmpty(chunk.Digest))
            throw new InvalidDataException("The runtime diagnostic stream is invalid.");

        string decoded;
        try { decoded = StrictUtf8.GetString(chunk.Content.Span); }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The runtime diagnostic stream is not valid UTF-8.", exception);
        }
        Capture(decoded);
        _nextSequence++;
        return Task.CompletedTask;
    }

    private void Capture(string text)
    {
        var clean = new string(text
            .Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t')
            .ToArray());
        // Guests send rolling snapshots. Retain the most recent error header before
        // ordinary lease/HTTP logging pushes it out of the final diagnostic tail.
        var failureStart = Math.Max(clean.LastIndexOf("fail: ", StringComparison.Ordinal),
            clean.LastIndexOf("crit: ", StringComparison.Ordinal));
        if (failureStart >= 0)
        {
            var failureEnd = clean.Length;
            foreach (var prefix in new[] { "info: ", "warn: ", "dbug: ", "trce: " })
            {
                var next = clean.IndexOf(prefix, failureStart + 6, StringComparison.Ordinal);
                if (next >= 0) failureEnd = Math.Min(failureEnd, next);
            }
            _lastFailure = clean.Substring(failureStart, Math.Min(failureEnd - failureStart, 4096));
        }
        var header = _lastFailure is null ? "" : $"Last reported failure:\n{_lastFailure}\nLatest runtime output:\n";
        Latest = header + new string(clean.TakeLast(MaximumDiagnosticCharacters - header.Length).ToArray());
    }
}
