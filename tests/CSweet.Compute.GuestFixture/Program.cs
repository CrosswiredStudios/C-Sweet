using System.Diagnostics;
using System.Text.Json;

switch (args.FirstOrDefault())
{
    case "arguments":
        Console.Write(JsonSerializer.Serialize(args.Skip(1).ToArray()));
        return 7;
    case "environment":
        Console.Write(JsonSerializer.Serialize(new[] { Environment.GetEnvironmentVariable(args[1]),
            Environment.GetEnvironmentVariable("SystemRoot"), Environment.GetEnvironmentVariable("TEMP") }));
        return 0;
    case "output":
        Console.Write(new string('a', 20000)); Console.Error.Write(new string('b', 20000));
        return 0;
    case "spawn":
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("hold");
        using (var child = Process.Start(start)!) Console.WriteLine(child.Id);
        return 0;
    case "stdin":
        Console.Write((await Console.In.ReadToEndAsync()).Length); return 0;
    case "hold":
        await Task.Delay(Timeout.Infinite);
        return 0;
    default: return 2;
}

