using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed class ConnectionServiceSelectionStore(CSweetDbContext db, IOptions<PluginConnectionOptions> options, IHttpClientFactory? clients = null)
{
    public ConnectionServiceChoice Current()
    {
        var json = db.SystemConfigurations.AsNoTracking().OrderBy(x => x.CreatedAt).Select(x => x.ConnectionServiceSelectionJson).FirstOrDefault();
        if (json is not null) return JsonSerializer.Deserialize<ConnectionServiceChoice>(json) ?? throw new InvalidOperationException("Invalid stored service choice.");
        var configured = options.Value.Managed;
        return new(configured.Enabled ? ManagedServiceTrust.Identity(configured.ServiceOrigin) == "Official" ? "Official" : "SelfHosted" : "Skip", configured.ServiceOrigin, 0);
    }
    public ManagedPluginConnectionOptions Effective(ManagedPluginConnectionOptions configured)
    {
        var choice = Current();
        return new() { Enabled = choice.Mode != "Skip", ServiceOrigin = choice.Origin ?? configured.ServiceOrigin,
            RegistrationPath = configured.RegistrationPath, ReturnUri = configured.ReturnUri, CustomerOwnedProfiles = configured.CustomerOwnedProfiles };
    }
    public async Task<ConnectionServiceProbe> Probe(CancellationToken ct)
    {
        var choice = Current();
        if (choice.Mode == "Skip") return new("Skipped", false, false, false);
        try
        {
            using var http = (clients ?? throw new InvalidOperationException()).CreateClient(nameof(ConnectionServiceOwnerGateway));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(http.Timeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromSeconds(30) : http.Timeout);
            ct = deadline.Token;
            var origin = ManagedServiceTrust.CanonicalOrigin(choice.Origin!);
            using var response = await http.GetAsync(origin + "/service-info", HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return new("UnavailableOrUnsupported", false, false, false);
            using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[1024];
            int count; while ((count = await input.ReadAsync(buffer, ct)) > 0) { if (output.Length + count > 8192) throw new InvalidOperationException(); output.Write(buffer, 0, count); }
            var info = JsonSerializer.Deserialize<CSweet.ConnectionService.Protocol.ConnectionServiceCapabilities>(output.ToArray(), CSweet.ConnectionService.Protocol.DeploymentProofCodec.Json);
            if (info is null || info.Origin != origin || info.Protocol != CSweet.ConnectionService.Protocol.ConnectionProtocol.Version) return new("Unsupported", false, false, false);
            // A remote advertisement cannot enable unimplemented native mobile or action clients.
            return new(info.OAuthAccountSetup ? "OAuthSetupAvailable" : "Unsupported", info.OAuthAccountSetup, false, false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException or IOException)
        { return new("UnavailableOrUnsupported", false, false, false); }
    }
    public async Task<ConnectionServiceChoice> Save(Guid actor, SaveConnectionServiceChoice request, CancellationToken ct)
    {
        if (actor == Guid.Empty || request.Mode is not ("Official" or "SelfHosted" or "Skip")) throw new ArgumentException("Choose a connection service or skip.");
        await new SetupService(db).EnsureSeededAsync(ct);
        var config = await db.SystemConfigurations.OrderBy(x => x.CreatedAt).FirstAsync(ct);
        var current = Current(); if (current.Revision != request.ExpectedRevision) throw new InvalidOperationException("Service choice changed. Reload before saving.");
        var origin = request.Mode switch { "Official" => ManagedServiceTrust.OfficialOrigin, "SelfHosted" => ManagedServiceTrust.CanonicalOrigin(request.Origin ?? ""), _ => current.Origin };
        // Keep the old protected key and cleanup route. A different live origin cannot inherit its authority.
        if (origin != current.Origin && File.Exists(options.Value.Managed.RegistrationPath))
            throw new InvalidOperationException("Retire existing connections and preserve their cleanup registration before an operator prepares a different service binding.");
        var next = new ConnectionServiceChoice(request.Mode, origin, current.Revision + 1);
        config.ConnectionServiceSelectionJson = JsonSerializer.Serialize(next);
        db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), EventType = "connections.selection.changed", EntityType = nameof(SystemConfiguration), EntityId = config.Id,
            Summary = $"Connection service choice {request.Mode}; actor {actor:D}; revision {next.Revision}", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct); return next;
    }
}
