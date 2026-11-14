using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSweet.Mobile.Transport;

/// <summary>Phone → Headquarters, first message on a new secure channel.</summary>
public sealed record PhoneHello(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("protocol")] int Protocol);

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
    private const int MaxLine = 4096;

    public static Task SendHelloAsync(Stream stream, CancellationToken ct) =>
        WriteLineAsync(stream, new PhoneHello("hello", Protocol), ct);

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

    private static async Task WriteLineAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length >= MaxLine) throw new InvalidDataException("Message too large.");
        await stream.WriteAsync(bytes, ct);
        await stream.WriteAsync("\n"u8.ToArray(), ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<T> ReadLineAsync<T>(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxLine];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length) throw new InvalidDataException("Message too large.");
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), ct);
            if (read == 0) throw new EndOfStreamException();
            if (buffer[length] == (byte)'\n') break;
            length++;
        }
        return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length)) ?? throw new InvalidDataException("Empty message.");
    }
}
