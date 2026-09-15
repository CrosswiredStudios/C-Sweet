using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CSweet.Compute.Guest;
using CSweet.Compute.Runtime;

namespace CSweet.UnitTests;

public sealed class ComputePortPublishingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Published_link_serves_the_live_guest_app_and_closes_on_removal(bool untilReleased)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var app = new TcpListener(IPAddress.Loopback, 0);
        app.Start();
        var guestPort = ((IPEndPoint)app.LocalEndpoint).Port;
        var appTask = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await app.AcceptTcpClientAsync(deadline.Token);
                using var reader = new StreamReader(client.GetStream(), leaveOpen: true);
                while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
                await client.GetStream().WriteAsync("HTTP/1.0 200 OK\r\nContent-Length: 12\r\nContent-Type: text/plain\r\n\r\nHello World!"u8.ToArray(), deadline.Token);
            }
        });
        var guests = new List<Task>();
        async Task<Stream> Connect(CancellationToken token)
        {
            var name = "csweet-forward-test-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            var accepting = server.WaitForConnectionAsync(token);
            await client.ConnectAsync(token); await accepting;
            guests.Add(Task.Run(async () =>
            {
                await using (server)
                {
                    var execution = new ComputeGuestExecution("linux", _ => throw new Exception("Forwarding cannot launch a process."));
                    try { await new ComputeGuestConnection(execution).ServeAsync(server, deadline.Token); }
                    catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                }
            }));
            return client;
        }
        using var publisher = new ComputeLocalPortPublisher();
        var environment = Guid.NewGuid();
        var publication = await publisher.PublishAsync(environment, guestPort, untilReleased ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddMinutes(1), Connect, deadline.Token);
        Assert.StartsWith("http://127.0.0.1:", publication.Url);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        Assert.Equal("Hello World!", await http.GetStringAsync(publication.Url, deadline.Token));
        await appTask;
        publisher.Remove(environment);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync(publication.Url, deadline.Token));
        await Task.WhenAll(guests).WaitAsync(deadline.Token);
    }
}
