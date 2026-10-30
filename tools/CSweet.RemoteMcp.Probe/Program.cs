using CSweet.RemoteMcp;
using CSweet.RemoteMcp.Probe;

// This executable deliberately has no configuration, key argument, logger, environment-key
// lookup, database, listener or key-ring persistence. No exception text is emitted.
// Usage: CSweet.RemoteMcp.Probe --manifest <reviewed csweet-plugin.json> [--stdin]
var stdin = args.Length == 3 && args[2] == "--stdin";
if (args.Length is not (2 or 3) || args[0] != "--manifest" || args.Length == 3 && !stdin)
{
    Console.Error.WriteLine("Usage: CSweet.RemoteMcp.Probe --manifest <reviewed csweet-plugin.json> [--stdin]. Never supply a key as an argument.");
    return 2;
}
ReviewedRemoteMcpProfile profile;
try { profile = ReviewedRemoteMcpProfile.FromManifestJson(File.ReadAllText(args[1])); }
catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
{
    Console.Error.WriteLine("The manifest is unreadable or is not a valid reviewed remote MCP declaration.");
    return 2;
}
if (stdin && !Console.IsInputRedirected)
{
    Console.Error.WriteLine("Secure stdin mode requires redirected input from a trusted sender.");
    return 2;
}
if (!stdin && Console.IsInputRedirected)
{
    Console.Error.WriteLine("Interactive mode requires a terminal with hidden input.");
    return 2;
}
using var expiry = new CancellationTokenSource(TimeSpan.FromMinutes(10));
using var watchdog = new Timer(_ => { Console.Error.WriteLine("status=expired"); Environment.Exit(124); }, null, TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; expiry.Cancel(); };
string key = "";
try
{
    if (!Console.IsInputRedirected)
    {
        Console.WriteLine($"Endpoint {profile.Host}: one read-only search (limit 1), at most one lookup, then temporary local grant revocation. No provider identity verification.");
        Console.Write("Temporary API key (hidden): ");
    }
    key = await ReadKey(expiry.Token);
    var run = new ProbeRunner().RunAsync(profile, key, expiry.Token);
    key = "";
    var result = await run;
    Console.WriteLine($"status={result.Status}; searchCount={result.SearchCount}; lookupSucceeded={result.LookupSucceeded}; localRevocationVerified={result.RevocationVerified}; failureCategory={result.FailureCategory ?? "none"}; failureStage={result.FailureStage ?? "none"}; httpStatus={result.HttpStatus?.ToString() ?? "none"}");
    return result.Status == "passed" && result.RevocationVerified ? 0 : 1;
}
catch (OperationCanceledException) { Console.Error.WriteLine("status=cancelled"); return 124; }
catch { Console.Error.WriteLine("status=invalid-input-or-failed"); return 1; }
finally { key = ""; }

static async Task<string> ReadKey(CancellationToken ct)
{
    var characters = new char[256]; var count = 0;
    try
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            char value;
            if (Console.IsInputRedirected)
            {
                var one = new char[1];
                try
                {
                    if (await Console.In.ReadAsync(one.AsMemory(), ct) == 0) throw new InvalidOperationException();
                    value = one[0];
                }
                finally { Array.Clear(one); }
            }
            else
            {
                while (!Console.KeyAvailable) await Task.Delay(50, ct);
                var pressed = Console.ReadKey(intercept: true);
                if (pressed.Key == ConsoleKey.Backspace) { if (count > 0) characters[--count] = '\0'; continue; }
                value = pressed.KeyChar;
            }
            if (value is '\r' or '\n') break;
            if (char.IsControl(value) || count == characters.Length) throw new InvalidOperationException();
            characters[count++] = value;
        }
        if (!Console.IsInputRedirected) Console.WriteLine();
        return new string(characters, 0, count);
    }
    finally { Array.Clear(characters); }
}
