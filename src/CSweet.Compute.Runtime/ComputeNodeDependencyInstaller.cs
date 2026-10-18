using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

/// <summary>Public package ingress without a VM network adapter. Host bytes are bounded and verified, never extracted or executed.</summary>
public static class ComputeNodeDependencyInstaller
{
    public static async Task<ComputeWorkloadResult> ExecuteAsync(ComputeNodeDependencies input, ComputeGuestCommand command, Guid operationId,
        Func<CancellationToken, Task<Stream>> connect, Func<CancellationToken, Task<bool>> authorize, CancellationToken token,
        Func<Uri, CancellationToken, Task<byte[]>>? download = null)
    {
        var dependencies = input.Validate(); command = command.ValidateAndSnapshot("linux");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(7));
        var ct = deadline.Token; long total = 0; var sequence = 0;
        using var http = download is null ? CreateClient() : null;
        async Task<bool> Current() => await authorize(ct);
        async Task<ComputeGuestWireResult> Run(string script, int timeout = 30) => await ComputeGuestCommandClient.ExecuteAsync(connect,
            new(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"npm:{operationId:N}:{sequence++}")).AsSpan(0, 16)),
                "/bin/sh", "/var/lib/csweet-compute/work", ["-c", "set -eu\n" + script], timeout, 8192), "linux", ct);
        static bool Passed(ComputeGuestWireResult result) => result is { ExitCode: 0, TimedOut: false, ErrorCode: null };
        async Task<ComputeGuestWireResult> WriteChunk(string script)
        {
            // Only fixed-offset writes of the same verified bytes may repeat after a lost reply.
            // Never retry cache population, npm installation or the caller's command this way.
            for (var attempt = 0; ; attempt++)
            {
                if (attempt > 0 && !await Current()) throw new UnauthorizedAccessException();
                try { return await Run(script); }
                catch (Exception error) when (attempt < 2 && !ct.IsCancellationRequested &&
                    error is IOException or SocketException)
                { await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), ct); }
            }
        }
        try
        {
            foreach (var package in dependencies.Packages)
            {
                if (!await Current()) return new(ErrorCode: "dependency-authority-denied");
                byte[] bytes;
                using (var fetch = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    fetch.CancelAfter(TimeSpan.FromSeconds(30));
                    bytes = download is null ? await DownloadAsync(http!, new(package.Url), fetch.Token) : await download(new(package.Url), fetch.Token);
                }
                total = checked(total + bytes.Length);
                if (bytes.Length > ComputeNodeDependencies.MaximumPackageBytes || total > ComputeNodeDependencies.MaximumTotalBytes)
                    return new(ErrorCode: "dependency-fetch-failed");
                if (!CryptographicOperations.FixedTimeEquals(SHA512.HashData(bytes), Convert.FromBase64String(package.Integrity[7..])))
                    return new(ErrorCode: "dependency-integrity-failed");
                if (!await Current()) return new(ErrorCode: "dependency-authority-denied");
                var file = dependencies.GuestDirectory + "/packages/" + ComputeNodeDependencies.FileName(package);
                var sha = Convert.ToHexStringLower(SHA512.HashData(bytes));
                // A prior interrupted operation may already have transferred this exact tarball.
                // Recheck its length and integrity under current authority before reusing any bytes.
                if (Passed(await Run($"test -f {file} && test ! -L {file} && test \"$(stat -c %s {file})\" -eq {bytes.Length} && printf '%s  {file}\\n' '{sha}' | sha512sum -c -\n")))
                    continue;
                if (!Passed(await Run($"mkdir -p {dependencies.GuestDirectory}/packages\n: > {file}\n"))) return new(ErrorCode: "guest-unavailable");
                for (var offset = 0; offset < bytes.Length; offset += 18000)
                {
                    // Bound the authority window during a large transfer as well as before each download.
                    if (offset % (18000 * 32) == 0 && !await Current()) return new(ErrorCode: "dependency-authority-denied");
                    var count = Math.Min(18000, bytes.Length - offset);
                    if (!Passed(await WriteChunk($"printf '%s' '{Convert.ToBase64String(bytes, offset, count)}' | base64 -d | dd of={file} bs=18000 seek={offset / 18000} conv=notrunc status=none\n")))
                        return new(ErrorCode: "guest-unavailable");
                }
                if (!Passed(await Run($"printf '%s  {file}\\n' '{sha}' | sha512sum -c -\n"))) return new(ErrorCode: "dependency-integrity-failed");
            }
            if (!await Current()) return new(ErrorCode: "dependency-authority-denied");
            var populate = $"mkdir -p {dependencies.GuestDirectory}/cache\n";
            if (dependencies.Packages.Count > 0)
                populate += $"docker run --rm --network=none -v {dependencies.GuestDirectory}:/deps csweet/node:22 sh -c 'npm cache add --offline --ignore-scripts --no-audit --no-fund --cache /deps/cache /deps/packages/*.tgz'\n";
            if (!Passed(await Run(populate, 120))) return new(ErrorCode: "dependency-fetch-failed");
            if (!await Current()) return new(ErrorCode: "dependency-authority-denied");
            return new(Command: await ComputeGuestCommandClient.ExecuteAsync(connect, command, "linux", ct));
        }
        catch (UnauthorizedAccessException) { return new(ErrorCode: "dependency-authority-denied"); }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or OverflowException ||
            error is OperationCanceledException && !token.IsCancellationRequested)
        { return new(ErrorCode: "dependency-fetch-failed"); }
    }

    private static HttpClient CreateClient()
    {
        var policy = new ComputePublicInternetPolicy();
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, token) =>
            {
                if (context.DnsEndPoint.Host != ComputeNodeDependencies.Registry || context.DnsEndPoint.Port != 443)
                    throw new UnauthorizedAccessException("dependency-destination-denied");
                var address = await policy.ResolveAsync(context.DnsEndPoint.Host, token);
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, 443), token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            } };
        return new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    private static async Task<byte[]> DownloadAsync(HttpClient client, Uri uri, CancellationToken token)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count != 0 ||
            response.Content.Headers.ContentLength > ComputeNodeDependencies.MaximumPackageBytes) throw new InvalidDataException("dependency-response-invalid");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); var buffer = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > ComputeNodeDependencies.MaximumPackageBytes) throw new InvalidDataException("dependency-response-too-large");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
