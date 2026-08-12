using CSweet.AgentRuntime.Guest;
using CSweet.AgentRuntime.Protocol;

var transportName = Environment.GetEnvironmentVariable("CSWEET_GUEST_BROKER_TRANSPORT") ?? "stdio";
var usesHostBootChannel = transportName is "hyperv-vsock" or "firecracker-vsock";
IGuestBrokerTransport transport = transportName switch
{
    "stdio" => new StandardIoGuestBrokerTransport(),
    "hyperv-vsock" => new LinuxHyperVSocketGuestTransport(),
    "firecracker-vsock" => new LinuxHyperVSocketGuestTransport(ReadFirecrackerPort()),
    _ => throw new InvalidOperationException("The configured guest broker transport is unsupported.")
};
await using var connection = await transport.AcceptAsync();
try
{
    GuestServiceOptions options;
    try
    {
        options = !usesHostBootChannel
            ? GuestServiceOptions.FromEnvironment()
            : GuestServiceOptions.FromBootConfiguration(
                await GuestBootConfigurationReader.ReadAsync(connection.Input));
        options.Validate(TimeProvider.System);
        if (options.WorkloadKind == 1)
        {
            var artifactDigest = options.ArtifactDigest ??
                throw new InvalidDataException("A runtime guest requires artifact media.");
            var destinationRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(options.ArtifactRoot)) ??
                throw new InvalidDataException("The runtime artifact root is invalid.");
            await new GuestArtifactMaterializer().MaterializeAsync(artifactDigest, destinationRoot);
        }
    }
    catch (Exception exception) when (usesHostBootChannel)
    {
        var reason = exception switch
        {
            InvalidDataException or InvalidOperationException or FormatException => "guest-boot-invalid",
            IOException => "guest-boot-io-failed",
            _ => "guest-boot-failed"
        };
        var detail = new string(exception.Message
            .Where(character => !char.IsControl(character) || character == ' ')
            .Take(512)
            .ToArray());
        try
        {
            await LengthDelimitedProtobuf.WriteAsync(connection.Output, new GuestEnvelope
            {
                ProtocolVersion = "1.0",
                MessageId = Guid.NewGuid().ToString("N"),
                BootFailure = new GuestBootFailure { ReasonCode = reason, Detail = detail }
            }, cancellationToken: CancellationToken.None);
        }
        catch { }
        throw;
    }

    await using var workload = new GuestWorkloadSupervisor(options);
    var session = new GuestBrokerSession(options, workload, TimeProvider.System);
    await session.RunAsync(
        connection.Input,
        connection.Output,
        CancellationToken.None);
}
finally
{
    if (usesHostBootChannel)
        await GuestSystemPower.PowerOffAsync();
}

static int ReadFirecrackerPort()
{
    var value = Environment.GetEnvironmentVariable("CSWEET_GUEST_VSOCK_PORT") ?? "5000";
    return int.TryParse(value, System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out var port) && port is >= 1024 and <= 65535
        ? port
        : throw new InvalidOperationException("The configured Firecracker guest broker port is invalid.");
}
