using System.Text.Json;
using System.Text.Json.Serialization;
using CSweet.AgentRuntime.Core;
using CSweet.AgentRuntime.HyperV.Helper;

const int maximumRequestBytes = 1024 * 1024;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

PlatformHelperResponse response;
try
{
    var arguments = HelperArguments.Parse(args);
    if (!string.Equals(arguments.ProtocolVersion, "1.0", StringComparison.Ordinal))
        throw new HelperProtocolException("unsupported-protocol", "The helper protocol version is not supported.");
    using var input = new StreamReader(Console.OpenStandardInput());
    var payload = await ReadBoundedAsync(input, maximumRequestBytes);
    var request = JsonSerializer.Deserialize<PlatformHelperRequest>(payload, json) ?? new PlatformHelperRequest();
    var controller = new HyperVHelperController(HyperVHelperPaths.Resolve());
    response = await controller.ExecuteAsync(arguments.Operation, request);
}
catch (HelperProtocolException exception)
{
    response = new PlatformHelperResponse
    {
        Success = false,
        ErrorCode = exception.ErrorCode,
        SanitizedError = exception.Message
    };
}
catch (Exception)
{
    response = new PlatformHelperResponse
    {
        Success = false,
        ErrorCode = "helper-failure",
        SanitizedError = "The Hyper-V helper failed unexpectedly."
    };
}

await Console.Out.WriteAsync(JsonSerializer.Serialize(response, json));
// Protocol-level rejections are valid helper responses. A non-zero process exit
// would cause RuntimeHost to discard the typed error and replace it with stderr.
return 0;

static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
{
    var buffer = new char[8192];
    var output = new System.Text.StringBuilder();
    while (true)
    {
        var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximumCharacters + 1 - output.Length)));
        if (read == 0) return output.ToString();
        output.Append(buffer, 0, read);
        if (output.Length > maximumCharacters)
            throw new HelperProtocolException("request-too-large", "The helper request exceeded its limit.");
    }
}
