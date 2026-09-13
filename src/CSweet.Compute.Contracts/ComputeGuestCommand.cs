using System.Text;

namespace CSweet.Compute.Contracts;

/// <summary>Guest-local command data. This is neither authorization nor a host process request.</summary>
public sealed record ComputeGuestCommand(Guid RequestId, string Executable, string WorkingDirectory,
    IReadOnlyList<string> Arguments, int TimeoutSeconds, int MaximumOutputBytes)
{
    public const int MaximumArguments = 64;
    public const int MaximumTextBytes = 32768;
    public const int MaximumTimeoutSeconds = 900;
    public const int MaximumCapturedBytes = 1048576;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Validate against the approved guest OS, independent of the broker's OS, and detach mutable input.
    /// Paths refer only to the guest. This lexical check is not a filesystem confinement boundary.
    /// </summary>
    public ComputeGuestCommand ValidateAndSnapshot(string operatingSystem)
    {
        if (RequestId == Guid.Empty || TimeoutSeconds is < 1 or > MaximumTimeoutSeconds ||
            MaximumOutputBytes is < 1 or > MaximumCapturedBytes || Arguments is null ||
            Arguments.Count > MaximumArguments || !AbsoluteGuestPath(Executable, operatingSystem) ||
            !AbsoluteGuestPath(WorkingDirectory, operatingSystem))
            throw new ArgumentException("Guest command is invalid.");

        var arguments = Arguments.ToArray();
        if (arguments.Length > MaximumArguments) throw new ArgumentException("Guest command is invalid.");
        long bytes = TextBytes(Executable) + TextBytes(WorkingDirectory);
        foreach (var argument in arguments) bytes += TextBytes(argument);
        if (bytes > MaximumTextBytes) throw new ArgumentException("Guest command exceeds its text limit.");
        return this with { Arguments = Array.AsReadOnly(arguments) };
    }

    private static int TextBytes(string? value)
    {
        if (value is null || value.Length > MaximumTextBytes || value.Contains('\0'))
            throw new ArgumentException("Guest command text is invalid.");
        try { return StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw new ArgumentException("Guest command text is invalid."); }
    }

    private static bool AbsoluteGuestPath(string? path, string operatingSystem)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        // UNC/device paths are excluded: selecting a command must not implicitly select a network share.
        return operatingSystem switch
        {
            "linux" => path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal),
            "windows" => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' &&
                path[2] is '\\' or '/' && !path.AsSpan(2).Contains(':'),
            _ => false
        };
    }
}
