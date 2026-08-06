namespace CSweet.AgentRuntime.HyperV.Helper;

internal sealed record HelperArguments(string ProtocolVersion, string Operation)
{
    private static readonly HashSet<string> AllowedOperations =
        ["probe", "create", "start", "inspect", "stop", "destroy", "logs"];

    public static HelperArguments Parse(string[] args)
    {
        string? protocol = null;
        string? operation = null;
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
                throw new HelperProtocolException("invalid-arguments", "The helper arguments are incomplete.");
            if (args[index] == "--protocol") protocol = args[index + 1];
            else if (args[index] == "--operation") operation = args[index + 1];
            else throw new HelperProtocolException("invalid-arguments", "The helper received an unsupported argument.");
        }
        if (string.IsNullOrWhiteSpace(protocol) || operation is null || !AllowedOperations.Contains(operation))
            throw new HelperProtocolException("invalid-arguments", "The helper arguments are invalid.");
        return new HelperArguments(protocol, operation);
    }
}

internal sealed class HelperProtocolException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
