using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace CSweet.AgentHost.Broker;

public sealed class PlatformWebProxyCapabilityHandler(
    CSweetDbContext db,
    IPluginSecretStore secrets,
    IPluginOAuthTokenBroker tokenBroker,
    IAuditEventWriter audit,
    ILogger<PlatformWebProxyCapabilityHandler> logger)
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ForwardedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Language", "If-Modified-Since", "If-None-Match", "User-Agent"
    };

    public async Task<CapabilityResult> HandleAsync(
        AgentSession session,
        RequestCapability request,
        CancellationToken cancellationToken)
    {
        if (request.Capability is not (PluginPlatformCapabilities.WebFetch or PluginPlatformCapabilities.WebRequest))
            return Failure(request.RequestId, "Unsupported web proxy capability.");
        if (request.Payload.Length > 64 * 1024)
            return Failure(request.RequestId, "The web proxy request exceeds the 64 KB limit.");
        if (session.Grant.RequestedCapabilities?.Contains(request.Capability) != true)
            return Failure(request.RequestId, $"The installation is not granted {request.Capability}.");
        if (!Guid.TryParse(session.InstallationId, out var installationId))
            return Failure(request.RequestId, "The plugin installation identity is invalid.");

        PlatformWebFetchRequest? input;
        try { input = JsonSerializer.Deserialize<PlatformWebFetchRequest>(request.Payload.Span, JsonOptions); }
        catch (JsonException) { return Failure(request.RequestId, "The web proxy request is not valid JSON."); }
        if (input is null || !Uri.TryCreate(input.Url, UriKind.Absolute, out var initialUri))
            return Failure(request.RequestId, "An absolute HTTP(S) URL is required.");
        var fetch = request.Capability == PluginPlatformCapabilities.WebFetch;
        if (fetch && input.Method is not ("GET" or "HEAD"))
            return Failure(request.RequestId, "web.fetch.v1 permits only GET and HEAD requests.");
        if (!fetch && input.Method is not ("POST" or "PUT" or "PATCH" or "DELETE"))
            return Failure(request.RequestId, "web.request.v1 permits only POST, PUT, PATCH, and DELETE requests.");
        if (input.Body?.Length > 1024 * 1024)
            return Failure(request.RequestId, "The web request body exceeds the 1 MB limit.");

        var installation = await db.AgentInstallations.AsNoTracking()
            .Include(x => x.PackageVersion).Include(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == installationId && x.IsEnabled, cancellationToken);
        if (installation?.PackageVersion is null || installation.Grant is null)
            return Failure(request.RequestId, "The plugin installation is unavailable.");

        PluginManifest manifest;
        IReadOnlySet<string> grantedWeb;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion.ManifestJson, JsonOptions)
                ?? throw new JsonException();
            grantedWeb = (JsonSerializer.Deserialize<IReadOnlyList<string>>(installation.Grant.NetworkAccessJson, JsonOptions) ?? [])
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException) { return Failure(request.RequestId, "The approved web access policy is invalid."); }
        var blockedCidrs = OutboundNetworkPolicy.ParseCidrs(
            await db.AgentRuntimeGlobalSettings.AsNoTracking()
                .Select(x => x.BlockedNetworkCidrs)
                .SingleOrDefaultAsync(cancellationToken));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var current = initialUri;
        try
        {
            for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
            {
                var rule = Authorize(current, input.Method, input.Credential, input.Connection, manifest, grantedWeb,
                    installation.SetupState != CSweet.Domain.Setup.PluginSetupState.Ready);
                if (rule is null)
                    return await DeniedAsync(request.RequestId, installationId, current, "Destination is outside the approved web grant.", cancellationToken);

                var addresses = await Dns.GetHostAddressesAsync(current.DnsSafeHost, timeout.Token);
                if (addresses.Length == 0 || addresses.Any(x => OutboundNetworkPolicy.IsForbiddenAddress(x, blockedCidrs)))
                    return await DeniedAsync(request.RequestId, installationId, current, "Destination resolves to a private or reserved address.", cancellationToken);

                using var handler = CreatePinnedHandler(current, addresses[0]);
                using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                using var outbound = new HttpRequestMessage(new HttpMethod(input.Method), current);
                if (input.Body is { Length: > 0 })
                {
                    outbound.Content = new ByteArrayContent(input.Body);
                    outbound.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(input.ContentType ?? "application/json");
                }
                if (input.Headers is not null)
                {
                    foreach (var header in input.Headers.Where(x => ForwardedHeaders.Contains(x.Key)))
                        outbound.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                if (!string.IsNullOrWhiteSpace(input.Credential))
                {
                    var credential = manifest.Credentials.SingleOrDefault(x => x.Name == input.Credential);
                    if (credential is null || !OutboundNetworkPolicy.IsAllowedOrigin(current, credential.AllowedOrigins))
                        return Failure(request.RequestId, "The requested credential is not bound to this origin.");
                    var value = await secrets.GetAsync(installationId, input.Credential, timeout.Token);
                    if (string.IsNullOrWhiteSpace(value)) return Failure(request.RequestId, "The requested credential is not configured.");
                    outbound.Headers.TryAddWithoutValidation("Authorization", value);
                }
                if (!string.IsNullOrWhiteSpace(input.Connection))
                {
                    if (!string.IsNullOrWhiteSpace(input.Credential))
                        return Failure(request.RequestId, "A request cannot use both credential and OAuth connection bindings.");
                    var declaration = manifest.Connections.SingleOrDefault(x => x.Id == input.Connection);
                    if (declaration is null || !OutboundNetworkPolicy.IsAllowedOrigin(current, declaration.AllowedOrigins))
                        return Failure(request.RequestId, "The requested connection is not bound to this origin.");
                    var connection = await db.PluginConnections.AsNoTracking().SingleOrDefaultAsync(x =>
                        x.AgentInstallationId == installationId && x.DeclarationId == input.Connection &&
                        x.Status == CSweet.Domain.Setup.PluginConnectionStatus.Connected, timeout.Token);
                    if (connection is null) return Failure(request.RequestId, "The requested connection is unavailable.");
                    if (!string.IsNullOrWhiteSpace(connection.BoundResourceId) &&
                        !string.Equals(connection.BoundResourceId, input.BoundResourceId, StringComparison.Ordinal))
                        return Failure(request.RequestId, "The request is not bound to the installation's confirmed external resource.");
                    var accessToken = await tokenBroker.GetAccessTokenAsync(installationId, connection, timeout.Token);
                    if (accessToken is null) return Failure(request.RequestId, "The connection must be reauthorized.");
                    outbound.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                }

                using var response = await client.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                {
                    if (redirect == MaximumRedirects) return Failure(request.RequestId, "The web proxy redirect limit was exceeded.");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }

                var body = input.Method == "HEAD"
                    ? (Bytes: Array.Empty<byte>(), Truncated: false)
                    : await ReadBoundedAsync(response.Content, timeout.Token);
                if (!string.IsNullOrWhiteSpace(input.Connection) && !body.Truncated && body.Bytes.Length > 0)
                {
                    var declaration = manifest.Connections.Single(x => x.Id == input.Connection);
                    body = (await ExtractSecretFieldsAsync(installationId, declaration, body.Bytes, timeout.Token), false);
                }
                var result = new PlatformWebFetchResponse(
                    (int)response.StatusCode,
                    current.GetLeftPart(UriPartial.Path),
                    response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
                    body.Bytes,
                    body.Truncated);
                await audit.WriteAsync("plugin.web.fetch", "PluginInstallation", installationId,
                    $"Plugin fetched {current.Scheme}://{current.Host}{current.AbsolutePath} with status {(int)response.StatusCode}.",
                    JsonSerializer.Serialize(new { installationId, host = current.Host, path = current.AbsolutePath, method = input.Method, status = (int)response.StatusCode, bytes = body.Bytes.Length }),
                    cancellationToken);
                return Success(request.RequestId, result);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(request.RequestId, "The web proxy request timed out.");
        }
        catch (Exception exception) when (exception is HttpRequestException or SocketException or IOException)
        {
            logger.LogWarning(exception, "Platform web request failed for plugin {PluginId}.", session.AgentId);
            return Failure(request.RequestId, "The remote web request failed.");
        }
        return Failure(request.RequestId, "The web proxy request failed.");
    }

    private static PluginWebAccessRule? Authorize(Uri uri, string method, string? credential, string? connection,
        PluginManifest manifest, IReadOnlySet<string> grants, bool bootstrapOnly)
    {
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || uri.IsLoopback)
            return null;
        if (!bootstrapOnly && manifest.WebAccess.Mode == PluginWebAccessMode.AllPublic && grants.Contains("all-public"))
            return new PluginWebAccessRule { Scheme = uri.Scheme, Host = uri.Host, PathPrefix = "/", Methods = [method], Credential = credential, Connection = connection };
        foreach (var rule in manifest.WebAccess.Rules)
        {
            if (bootstrapOnly && !rule.Bootstrap) continue;
            if (!grants.Contains(CSweet.Infrastructure.Setup.AgentImportPreviewService.WebGrantToken(rule))) continue;
            if (!string.Equals(rule.Protocol, "http", StringComparison.Ordinal) ||
                !string.Equals(rule.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(OutboundNetworkPolicy.NormalizeHost(rule.Host), OutboundNetworkPolicy.NormalizeHost(uri.DnsSafeHost), StringComparison.Ordinal) ||
                rule.Port is not null && rule.Port != uri.Port ||
                !OutboundNetworkPolicy.IsPathWithinPrefix(uri.AbsolutePath, rule.PathPrefix) ||
                !rule.Methods.Contains(method, StringComparer.Ordinal) ||
                !string.Equals(rule.Credential, credential, StringComparison.Ordinal) ||
                !string.Equals(rule.Connection, connection, StringComparison.Ordinal)) continue;
            return rule;
        }
        return null;
    }

    private async Task<byte[]> ExtractSecretFieldsAsync(Guid installationId,
        PluginConnectionDeclaration declaration, byte[] body, CancellationToken cancellationToken)
    {
        if (declaration.SecretResponseFields.Count == 0) return body;
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { return body; }
        if (root is null) return body;
        foreach (var pointer in declaration.SecretResponseFields)
        {
            var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
            JsonNode? current = root;
            for (var index = 0; index < segments.Length - 1; index++) current = current?[segments[index]];
            if (current is not JsonObject parent || segments.Length == 0 || parent[segments[^1]] is not { } secretNode) continue;
            var secretValue = secretNode is JsonValue value && value.TryGetValue<string>(out var textValue)
                ? textValue : secretNode.ToJsonString();
            if (string.IsNullOrWhiteSpace(secretValue)) continue;
            var reference = $"plugin-secret:{Guid.NewGuid():N}";
            await secrets.SetAsync(installationId, $"response.{reference[14..]}", secretValue, cancellationToken);
            parent[segments[^1]] = new JsonObject { ["secretReference"] = reference };
            await audit.WriteAsync("plugin.response-secret.extracted", "PluginInstallation", installationId,
                $"Extracted a declared secret response field for connection {declaration.Id}.",
                JsonSerializer.Serialize(new { installationId, connection = declaration.Id, pointer, reference }), cancellationToken);
        }
        return JsonSerializer.SerializeToUtf8Bytes(root, JsonOptions);
    }

    private async Task<CapabilityResult> DeniedAsync(string requestId, Guid installationId, Uri uri, string reason, CancellationToken token)
    {
        await audit.WriteAsync("plugin.web.denied", "PluginInstallation", installationId,
            $"Denied plugin web request to {uri.Scheme}://{uri.Host}{uri.AbsolutePath}: {reason}",
            JsonSerializer.Serialize(new { installationId, host = uri.Host, path = uri.AbsolutePath, reason }), token);
        return Failure(requestId, reason);
    }

    private static SocketsHttpHandler CreatePinnedHandler(Uri uri, IPAddress address) => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        MaxConnectionsPerServer = 2,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, token) =>
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, uri.Port), token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    };

    private static bool IsRedirect(HttpStatusCode status) => (int)status is 301 or 302 or 303 or 307 or 308;

    private static async Task<(byte[] Bytes, bool Truncated)> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (output.Length <= MaximumResponseBytes)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) return (output.ToArray(), false);
            var allowed = (int)Math.Min(read, MaximumResponseBytes - output.Length);
            if (allowed > 0) output.Write(buffer, 0, allowed);
            if (allowed < read || output.Length == MaximumResponseBytes) return (output.ToArray(), true);
        }
        return (output.ToArray(), true);
    }

    private static CapabilityResult Success(string requestId, PlatformWebFetchResponse response) => new()
    {
        RequestId = requestId, Succeeded = true, ContentType = "application/json",
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions))
    };

    private static CapabilityResult Failure(string requestId, string error) => new()
    {
        RequestId = requestId, Succeeded = false, ContentType = "application/json", Error = error
    };

}
