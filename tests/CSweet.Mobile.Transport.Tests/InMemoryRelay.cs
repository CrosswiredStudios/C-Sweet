using System.Security.Cryptography;

namespace CSweet.Mobile.Transport.Tests;

public enum FrameAction { Deliver, Drop, Duplicate, Corrupt }

/// <summary>Stand-in for the mobile relay hub with the same routing semantics, plus frame fault injection.</summary>
internal sealed class InMemoryRelay
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Connection> _routes = new();
    private readonly Dictionary<string, (Connection Phone, Connection Headquarters)> _channels = new();
    private int _frames;

    /// <summary>Decides what happens to the nth frame (0-based, both directions).</summary>
    public Func<int, byte[], FrameAction> Faults { get; set; } = (_, _) => FrameAction.Deliver;
    public int Attachments { get; private set; }

    public Connection Connect() => new(this);

    internal sealed class Connection(InMemoryRelay relay) : IRelayConnection
    {
        public IRelayHandler Handler { get; private set; } = default!;
        public void SetHandler(IRelayHandler handler) => Handler = handler;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<string> ChallengeAsync(CancellationToken ct) => Task.FromResult(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));

        public Task AttachHeadquartersAsync(string routeId, string signature, CancellationToken ct)
        {
            lock (relay._gate) { relay._routes[routeId] = this; relay.Attachments++; }
            return Task.CompletedTask;
        }

        public async Task<string> OpenChannelAsync(string routeId, string ticket, string phoneKey, string signature, CancellationToken ct)
        {
            Connection headquarters;
            var id = Guid.NewGuid().ToString("N");
            lock (relay._gate)
            {
                if (!relay._routes.TryGetValue(routeId, out headquarters!)) throw new InvalidOperationException("headquarters_offline");
                relay._channels[id] = (this, headquarters);
            }
            var spki = Convert.FromBase64String(phoneKey.Replace('-', '+').Replace('_', '/').PadRight(phoneKey.Length + (4 - phoneKey.Length % 4) % 4, '='));
            var thumbprint = Convert.ToBase64String(SHA256.HashData(spki)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await headquarters.Handler.OnChannelOpenedAsync(id, thumbprint);
            return id;
        }

        public async Task SendFrameAsync(string channelId, byte[] data, CancellationToken ct)
        {
            Connection? peer;
            int index;
            lock (relay._gate)
            {
                if (!relay._channels.TryGetValue(channelId, out var pair)) throw new IOException("unknown channel");
                peer = pair.Phone == this ? pair.Headquarters : pair.Phone;
                index = relay._frames++;
            }
            switch (relay.Faults(index, data))
            {
                case FrameAction.Drop: return;
                case FrameAction.Duplicate: await peer.Handler.OnFrameAsync(channelId, data); await peer.Handler.OnFrameAsync(channelId, data); return;
                case FrameAction.Corrupt: var copy = (byte[])data.Clone(); copy[copy.Length / 2] ^= 0xFF; await peer.Handler.OnFrameAsync(channelId, copy); return;
                default: await peer.Handler.OnFrameAsync(channelId, data); return;
            }
        }

        public async Task CloseChannelAsync(string channelId)
        {
            Connection? peer = null;
            lock (relay._gate)
            {
                if (relay._channels.Remove(channelId, out var pair)) peer = pair.Phone == this ? pair.Headquarters : pair.Phone;
            }
            if (peer is not null) await peer.Handler.OnChannelClosedAsync(channelId, "closed");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public int OpenChannels { get { lock (_gate) return _channels.Count; } }
}
