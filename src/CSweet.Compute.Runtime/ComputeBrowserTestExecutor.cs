using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

public static class ComputeBrowserTestExecutor
{
    public static async Task<ComputeGuestWireResult> ExecuteAsync(ComputeBrowserTest browser,
        Func<ComputeGuestCommand, CancellationToken, Task<ComputeGuestWireResult>> execute, CancellationToken token)
    {
        browser = browser.Validate();
        ComputeGuestWireResult? result = null;
        try { return result = await execute(browser.Command(), token); }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var removed = await execute(new(Guid.NewGuid(), "/usr/bin/docker", "/var/lib/csweet-compute/work",
                ["rm", "--force", browser.ContainerName], 5, 1024), cleanup.Token);
            if (removed is not { ExitCode: 0, TimedOut: false, ErrorCode: null } && result?.ExitCode == 0)
                throw new IOException("Browser process teardown could not be confirmed; no test pass was accepted.");
        }
    }
}
