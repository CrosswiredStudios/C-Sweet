using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Compute.Contracts;
using CSweet.Compute.HyperV;
using CSweet.Compute.Runtime;

namespace CSweet.UnitTests;

public sealed class ComputeLocalSetupHandoffTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "compute-handoff-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly ComputeLocalSetupHandoff handoff = new(1, Guid.NewGuid(), Guid.NewGuid(), new('a', 64),
        "https://localhost:47082/", new('b', 64), new("core", "public-key"), "C:\\source\\csweet", Guid.NewGuid());

    [Fact]
    public async Task ApiHandoffRoundTripsThroughTheActualInstallerReaderWithAttemptIdentity()
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(handoff, ComputeProtocol.Json));
        Assert.Equal(handoff, await LocalComputeInstaller.ReadHandoffAsync(path, default));
    }

    [Fact]
    public async Task LegacyHandoffWithoutAttemptIdentityRemainsReadable()
    {
        var document = JsonSerializer.SerializeToNode(handoff, ComputeProtocol.Json)!.AsObject();
        document.Remove("attemptId");
        await File.WriteAllTextAsync(path, document.ToJsonString());
        Assert.Equal(handoff with { AttemptId = null }, await LocalComputeInstaller.ReadHandoffAsync(path, default));
    }

    [Fact]
    public async Task UnknownFieldsAreStillRejectedRatherThanRelaxingTheProtocol()
    {
        var document = JsonSerializer.SerializeToNode(handoff, ComputeProtocol.Json)!.AsObject();
        document["unexpected"] = "untrusted";
        await File.WriteAllTextAsync(path, document.ToJsonString());
        await Assert.ThrowsAsync<JsonException>(() => LocalComputeInstaller.ReadHandoffAsync(path, default));
    }

    [Theory]
    [InlineData("attemptId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("setupId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("coreOrigin", "http://localhost/")]
    [InlineData("secret", "short")]
    public async Task InvalidHandoffTermsRemainRejected(string property, string value)
    {
        var document = JsonSerializer.SerializeToNode(handoff, ComputeProtocol.Json)!.AsObject();
        document[property] = value;
        await File.WriteAllTextAsync(path, document.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalComputeInstaller.ReadHandoffAsync(path, default));
    }

    public void Dispose() => File.Delete(path);
}
