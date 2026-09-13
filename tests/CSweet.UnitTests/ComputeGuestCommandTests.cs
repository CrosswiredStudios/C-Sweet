using System.Text.Json;
using CSweet.Compute.Contracts;

namespace CSweet.UnitTests;

public sealed class ComputeGuestCommandTests
{
    private static ComputeGuestCommand Command() => new(Guid.NewGuid(), "/usr/bin/dotnet", "/work",
        ["test", "--filter", "Name=Some test; $(literal)"], 60, 65536);

    [Fact]
    public void Arguments_remain_literal_and_are_detached_from_mutable_input()
    {
        var arguments = new[] { "a b", "; touch /tmp/example", "" };
        var snapshot = (Command() with { Arguments = arguments }).ValidateAndSnapshot("linux");
        arguments[0] = "changed";
        Assert.Equal(new[] { "a b", "; touch /tmp/example", "" }, snapshot.Arguments);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot.Arguments)[0] = "changed");
    }

    [Theory]
    [InlineData("linux", "/usr/bin/dotnet", "/work", true)]
    [InlineData("windows", "C:\\Tools\\dotnet.exe", "D:/work", true)]
    [InlineData("windows", "C:dotnet.exe", "D:/work", false)]
    [InlineData("windows", "\\\\server\\share\\tool.exe", "D:/work", false)]
    [InlineData("windows", "\\\\?\\C:\\tool.exe", "D:/work", false)]
    [InlineData("windows", "C:\\tool.exe:stream", "D:/work", false)]
    [InlineData("linux", "dotnet", "/work", false)]
    [InlineData("linux", "//server/tool", "/work", false)]
    [InlineData("linux", "/usr/bin/dotnet", "work", false)]
    [InlineData("unknown", "/usr/bin/dotnet", "/work", false)]
    public void Paths_are_validated_for_guest_platform(string os, string executable, string directory, bool valid)
    {
        var command = Command() with { Executable = executable, WorkingDirectory = directory };
        if (valid) Assert.Equal(executable, command.ValidateAndSnapshot(os).Executable);
        else Assert.Throws<ArgumentException>(() => command.ValidateAndSnapshot(os));
    }

    [Fact]
    public void Invalid_limits_and_malformed_text_are_rejected()
    {
        ComputeGuestCommand[] invalid =
        [
            Command() with { RequestId = Guid.Empty }, Command() with { TimeoutSeconds = 0 },
            Command() with { TimeoutSeconds = 901 }, Command() with { MaximumOutputBytes = 0 },
            Command() with { MaximumOutputBytes = 1048577 }, Command() with { Arguments = null! },
            Command() with { Arguments = Enumerable.Repeat("", 65).ToArray() },
            Command() with { Arguments = [null!] }, Command() with { Arguments = ["a\0b"] },
            Command() with { Arguments = ["\ud800"] },
            Command() with { Arguments = [new string('\u20ac', 12000)] }
        ];
        foreach (var command in invalid) Assert.Throws<ArgumentException>(() => command.ValidateAndSnapshot("linux"));
    }

    [Fact]
    public void Wire_contract_rejects_environment_and_host_selection_fields()
    {
        var json = JsonSerializer.Serialize(Command(), ComputeProtocol.Json);
        foreach (var field in new[] { "environment", "host", "dockerSocket", "shell" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ComputeGuestCommand>(
                json[..^1] + ",\"" + field + "\":\"unexpected\"}", ComputeProtocol.Json));
        var roundtrip = JsonSerializer.Deserialize<ComputeGuestCommand>(json, ComputeProtocol.Json)!;
        Assert.Equal(Command().Arguments, roundtrip.ValidateAndSnapshot("linux").Arguments);
    }
}
