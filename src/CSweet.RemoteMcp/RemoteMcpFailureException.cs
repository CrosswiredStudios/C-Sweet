namespace CSweet.RemoteMcp;

public enum RemoteMcpStage { Dns, Initialize, Initialized, ToolsList, Search, Lookup }
public enum RemoteMcpFailureCategory
{
    AuthenticationRejected, AccessPolicyRejected, RedirectRejected, RateLimited, ProviderHttpError, HttpRejected,
    DnsAddressPolicy, DnsFailure, TlsFailure, ConnectionFailure, TransportFailure, Timeout,
    ProtocolMismatch, ToolSchemaRejected, ResultSchemaRejected, InvalidJson, ResponseRejected
}

/// <summary>Host-generated allowlisted diagnostics only. Never carries an inner exception or remote content.</summary>
public sealed class RemoteMcpFailureException : InvalidOperationException
{
    public RemoteMcpStage Stage { get; }
    public RemoteMcpFailureCategory Category { get; }
    public int? HttpStatus { get; }
    internal RemoteMcpFailureException(RemoteMcpStage stage, RemoteMcpFailureCategory category, int? httpStatus = null)
        : base("The provider request failed or its response was withheld. Check the account connection in native settings.")
    { Stage = stage; Category = category; HttpStatus = httpStatus; }
}
