using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSweet.AgentRuntime.Core;
using CSweet.AgentRuntime.Firecracker.Helper;

const int maximumRequestBytes = 1024 * 1024;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};
var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var responseCommitted = false;

try
{
    var arguments = HelperArguments.Parse(args);
    if (!string.Equals(arguments.ProtocolVersion, "1.0", StringComparison.Ordinal))
        throw new HelperProtocolException("unsupported-protocol", "The helper protocol version is not supported.");
    var controller = new FirecrackerHelperController(FirecrackerHelperPaths.Resolve());
    if (arguments.Operation == "open-guest-channel")
    {
        var payload = await ReadLineAsync(input, maximumRequestBytes);
        var request = JsonSerializer.Deserialize<PlatformHelperRequest>(payload, json) ?? new PlatformHelperRequest();
        var opened = await controller.OpenGuestChannelAsync(request);
        await WriteResponseAsync(output, opened.Response, json, newline: true);
        responseCommitted = true;
        if (opened.Stream is not null)
            await BridgeAsync(input, output, opened.Stream);
    }
    else
    {
        var payload = await ReadToEndAsync(input, maximumRequestBytes);
        var request = JsonSerializer.Deserialize<PlatformHelperRequest>(payload, json) ?? new PlatformHelperRequest();
        var response = await controller.ExecuteAsync(arguments.Operation, request);
        await WriteResponseAsync(output, response, json, newline: false);
        responseCommitted = true;
    }
}
catch (HelperProtocolException exception)
{
    if (!responseCommitted)
        await WriteResponseAsync(output, new PlatformHelperResponse
        {
            Success = false,
            ErrorCode = exception.ErrorCode,
            SanitizedError = exception.Message
        }, json, newline: false);
}
catch (Exception)
{
    if (!responseCommitted)
        await WriteResponseAsync(output, new PlatformHelperResponse
        {
            Success = false,
            ErrorCode = "helper-failure",
            SanitizedError = "The Firecracker helper failed unexpectedly."
        }, json, newline: false);
}
return 0;

static async Task<byte[]> ReadToEndAsync(Stream stream, int maximumBytes)
{
    using var output = new MemoryStream();
    var buffer = new byte[8192];
    while (true)
    {
        var remaining = maximumBytes + 1 - (int)output.Length;
        if (remaining <= 0)
            throw new HelperProtocolException("request-too-large", "The helper request exceeded its limit.");
        var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)));
        if (read == 0) return output.ToArray();
        output.Write(buffer, 0, read);
    }
}

static async Task<byte[]> ReadLineAsync(Stream stream, int maximumBytes)
{
    using var output = new MemoryStream();
    var single = new byte[1];
    while (output.Length <= maximumBytes)
    {
        var read = await stream.ReadAsync(single);
        if (read == 0) throw new HelperProtocolException("invalid-request", "The helper request ended before its delimiter.");
        if (single[0] == (byte)'\n')
        {
            if (output.Length == 0) throw new HelperProtocolException("invalid-request", "The helper request was empty.");
            return output.ToArray();
        }
        if (single[0] is 0 or (byte)'\r')
            throw new HelperProtocolException("invalid-request", "The helper request framing is invalid.");
        output.WriteByte(single[0]);
    }
    throw new HelperProtocolException("request-too-large", "The helper request exceeded its limit.");
}

static async Task WriteResponseAsync(
    Stream stream,
    PlatformHelperResponse response,
    JsonSerializerOptions json,
    bool newline)
{
    var payload = JsonSerializer.SerializeToUtf8Bytes(response, json);
    await stream.WriteAsync(payload);
    if (newline) await stream.WriteAsync("\n"u8.ToArray());
    await stream.FlushAsync();
}

static async Task BridgeAsync(Stream input, Stream output, Stream guest)
{
    await using (guest)
    {
        var toGuest = input.CopyToAsync(guest);
        var fromGuest = guest.CopyToAsync(output);
        var completed = await Task.WhenAny(toGuest, fromGuest);
        await completed;
        var pending = ReferenceEquals(completed, toGuest) ? fromGuest : toGuest;
        _ = pending.ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
