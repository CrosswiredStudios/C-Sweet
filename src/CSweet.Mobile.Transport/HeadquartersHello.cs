using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSweet.Mobile.Transport;

/// <summary>
/// Phone → Headquarters, first message on a new secure channel. <see cref="Assertion"/> is the C-Sweet directory's
/// signed statement of which account owns this phone key for this installation; <see cref="DeviceLabel"/> is shown
/// to the person approving the phone.
/// </summary>
public sealed record PhoneHello(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("assertion")] string? Assertion = null,
    [property: JsonPropertyName("deviceLabel")] string? DeviceLabel = null);

/// <summary>
/// Phone → Headquarters on an enrolled session: one API request. A write carries a JSON body (base64, at most
/// <see cref="MaxBodyBytes"/>) and an operation key; Headquarters refuses writes without one.
/// </summary>
public sealed record MobileRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("contentType")] string? ContentType = null,
    [property: JsonPropertyName("body")] string? Body = null,
    [property: JsonPropertyName("operationKey")] string? OperationKey = null)
{
    /// <summary>JSON request bodies are capped at 256 KiB (operations register); files use a separate lane later.</summary>
    public const int MaxBodyBytes = 256 * 1024;
    public const int MaxOperationKeyLength = 160;
}

/// <summary>
/// Headquarters → phone. Before enrollment Headquarters says who it is and whether this phone still needs approval;
/// no business data crosses until the phone is enrolled.
/// </summary>
public sealed record HeadquartersWelcome(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("installationId")] Guid InstallationId,
    [property: JsonPropertyName("headquarters")] string Headquarters,
    [property: JsonPropertyName("phonePin")] string PhonePin,
    [property: JsonPropertyName("enrollment")] string Enrollment);

/// <summary>
/// The first exchange on a channel: one JSON line each way, at most 4 KiB. Proves the end-to-end path before the
/// request tunnel (TS-01/TS-03) exists.
/// </summary>
public static class HeadquartersHello
{
    public const int Protocol = 1;
    public const string EnrollmentRequired = "required";
    public const string Enrolled = "enrolled";
    /// <summary>Recorded, waiting for approval from a trusted session.</summary>
    public const string EnrollmentPending = "pending";
    public const string EnrollmentActive = "active";
    public const string EnrollmentDenied = "denied";
    public const string EnrollmentRevoked = "revoked";
    /// <summary>The assertion was missing, expired, for another installation or another key.</summary>
    public const string EnrollmentRejected = "rejected";
    /// <summary>The account in the assertion has no access to this Headquarters.</summary>
    public const string EnrollmentNoAccess = "no_access";
    private const int MaxLine = 4096;
    /// <summary>JSON responses are capped at 256 KiB, as in the operations register; files use a separate lane later.</summary>
    public const int MaxResponseBytes = 256 * 1024;

    public static Task SendHelloAsync(Stream stream, CancellationToken ct) =>
        WriteLineAsync(stream, new PhoneHello("hello", Protocol), ct);

    public static Task SendHelloAsync(Stream stream, string assertion, string deviceLabel, CancellationToken ct) =>
        WriteLineAsync(stream, new PhoneHello("hello", Protocol, assertion, deviceLabel), ct);

    public static Task SendWelcomeAsync(Stream stream, HeadquartersWelcome welcome, CancellationToken ct) =>
        WriteLineAsync(stream, welcome with { Type = "welcome", Protocol = Protocol }, ct);

    public static async Task<PhoneHello> ReadHelloAsync(Stream stream, CancellationToken ct)
    {
        var hello = await ReadLineAsync<PhoneHello>(stream, ct);
        if (hello.Type != "hello") throw new InvalidDataException("Expected hello.");
        return hello;
    }

    public static async Task<HeadquartersWelcome> ReadWelcomeAsync(Stream stream, CancellationToken ct)
    {
        var welcome = await ReadLineAsync<HeadquartersWelcome>(stream, ct);
        if (welcome.Type != "welcome") throw new InvalidDataException("Expected welcome.");
        return welcome;
    }

    private static async Task WriteLineAsync<T>(Stream stream, T value, CancellationToken ct, int maxLine = MaxLine)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length >= maxLine) throw new InvalidDataException("Message too large.");
        await stream.WriteAsync(bytes, ct);
        await stream.WriteAsync("\n"u8.ToArray(), ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<T> ReadLineAsync<T>(Stream stream, CancellationToken ct, int maxLine = MaxLine)
    {
        var buffer = new byte[Math.Min(maxLine, 8192)];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length >= maxLine) throw new InvalidDataException("Message too large.");
                Array.Resize(ref buffer, Math.Min(maxLine, buffer.Length * 2));
            }
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), ct);
            if (read == 0) throw new EndOfStreamException();
            if (buffer[length] == (byte)'\n') break;
            length++;
        }
        return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length)) ?? throw new InvalidDataException("Empty message.");
    }
}
