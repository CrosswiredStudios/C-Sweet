using System.Text.Json;

namespace CSweet.AgentHost.Broker;

public readonly struct JsonPayload
{
    private readonly byte[]? _bytes;

    private JsonPayload(byte[] bytes) => _bytes = bytes;

    public static JsonPayload Empty => new([]);
    public static JsonPayload From(byte[] bytes) => new(bytes);
    public static JsonPayload FromUtf8(string json) => new(System.Text.Encoding.UTF8.GetBytes(json));
    public static JsonPayload From<T>(T value, JsonSerializerOptions? options = null) =>
        new(JsonSerializer.SerializeToUtf8Bytes(value, options));

    public bool IsEmpty => _bytes is null || _bytes.Length == 0;
    public int Length => _bytes?.Length ?? 0;
    public ReadOnlySpan<byte> Span => _bytes ?? [];
    public byte[] ToByteArray() => _bytes?.ToArray() ?? [];
    public string ToStringUtf8() => System.Text.Encoding.UTF8.GetString(_bytes ?? []);
    public JsonElement ToElement() => IsEmpty
        ? JsonDocument.Parse("{}").RootElement.Clone()
        : JsonDocument.Parse(_bytes!).RootElement.Clone();
}

public sealed class RequestCapability
{
    public string RequestId { get; set; } = string.Empty;
    public string RequestingAgentId { get; set; } = string.Empty;
    public string Capability { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/json";
    public JsonPayload Payload { get; set; } = JsonPayload.Empty;
}

public sealed class CapabilityResult
{
    public string RequestId { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public string ContentType { get; set; } = "application/json";
    public JsonPayload Payload { get; set; } = JsonPayload.Empty;
    public string? Error { get; set; }
    public string? FailureCode { get; set; }
    public bool? Retryable { get; set; }
    public bool HasMore { get; set; }
    public int Sequence { get; set; }
}
