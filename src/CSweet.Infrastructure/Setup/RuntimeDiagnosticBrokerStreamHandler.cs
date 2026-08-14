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

    public string? Latest { get; private set; }

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
        Latest = new string(decoded
            .Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t')
            .TakeLast(MaximumDiagnosticCharacters)
            .ToArray());
        _nextSequence++;
        return Task.CompletedTask;
    }
}
