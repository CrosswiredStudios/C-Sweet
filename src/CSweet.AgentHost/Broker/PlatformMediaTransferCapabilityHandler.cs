using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.GenAi;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed class PlatformMediaTransferCapabilityHandler(
    CSweetDbContext db,
    IMediaAssetService mediaAssets,
    IPluginOAuthTokenBroker tokenBroker,
    IPluginSecretStore secrets,
    IAuditEventWriter audit,
    ILogger<PlatformMediaTransferCapabilityHandler> logger) : IPlatformCapabilityHandler
{
    private const int ChunkSize = 8 * 1024 * 1024;
    private const int MaximumMetadataBytes = 1024 * 1024;
    private const int MaximumResultBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool CanHandle(string capability) => capability == PluginPlatformCapabilities.MediaTransfer;

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        CapabilityResult result;
        try
        {
            result = await TransferAsync(session, request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = Failure(request.RequestId, "The media transfer timed out.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                           HttpRequestException or IOException or SocketException)
        {
            logger.LogWarning(exception, "Brokered media transfer failed for plugin {PluginId}.", session.AgentId);
            result = Failure(request.RequestId, exception is JsonException or InvalidOperationException
                ? exception.Message : "The provider media transfer failed.");
        }
        yield return result;
    }

    private async Task<CapabilityResult> TransferAsync(AgentSession session, RequestCapability request,
        CancellationToken cancellationToken)
    {
        if (session.Grant.RequestedCapabilities?.Contains(PluginPlatformCapabilities.MediaTransfer) != true)
            throw new InvalidOperationException("The installation is not granted brokered media transfer.");
        if (!Guid.TryParse(session.BusinessId, out var organizationId) ||
            !Guid.TryParse(session.InstallationId, out var installationId))
            throw new InvalidOperationException("The installation identity is invalid.");
        var input = JsonSerializer.Deserialize<MediaTransferInput>(request.Payload.Span, JsonOptions)
            ?? throw new JsonException("The media transfer payload is empty.");
        if (string.IsNullOrWhiteSpace(input.Connection) || string.IsNullOrWhiteSpace(input.BoundResourceId) ||
            string.IsNullOrWhiteSpace(input.IdempotencyKey) || input.IdempotencyKey.Length > 160 ||
            !Uri.TryCreate(input.InitiationUrl, UriKind.Absolute, out var initiationUri))
            throw new InvalidOperationException("The media transfer binding is incomplete.");
        var metadata = input.Metadata?.GetRawText() ?? "{}";
        if (Encoding.UTF8.GetByteCount(metadata) > MaximumMetadataBytes)
            throw new InvalidOperationException("The upload metadata exceeds the 1 MB limit.");

        var installation = await db.AgentInstallations.AsNoTracking()
            .Include(x => x.PackageVersion).Include(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == installationId && x.BusinessId == session.BusinessId &&
                                       x.IsEnabled && x.SetupState == PluginSetupState.Ready, cancellationToken);
        if (installation?.PackageVersion is null || installation.Grant is null)
            throw new InvalidOperationException("The plugin installation is unavailable.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion.ManifestJson, JsonOptions)
            ?? throw new JsonException("The approved plugin manifest is invalid.");
        var declaration = manifest.Connections.SingleOrDefault(x => x.Id == input.Connection)
            ?? throw new InvalidOperationException("The OAuth connection is not declared.");
        var grants = (JsonSerializer.Deserialize<IReadOnlyList<string>>(installation.Grant.NetworkAccessJson, JsonOptions) ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var rule = manifest.WebAccess.Rules.SingleOrDefault(x =>
            grants.Contains(AgentImportPreviewService.WebGrantToken(x)) &&
            string.Equals(x.Connection, input.Connection, StringComparison.Ordinal) &&
            x.Methods.Contains("POST", StringComparer.Ordinal) && IsWithin(initiationUri, x));
        if (rule is null || !OutboundNetworkPolicy.IsAllowedOrigin(initiationUri, declaration.AllowedOrigins))
            throw new InvalidOperationException("The resumable upload request is outside the approved web grant.");
        var connection = await db.PluginConnections.AsNoTracking().SingleOrDefaultAsync(x =>
            x.AgentInstallationId == installationId && x.DeclarationId == input.Connection &&
            x.Status == PluginConnectionStatus.Connected, cancellationToken)
            ?? throw new InvalidOperationException("The OAuth connection is unavailable.");
        if (!string.Equals(connection.BoundResourceId, input.BoundResourceId, StringComparison.Ordinal))
            throw new InvalidOperationException("The upload is not bound to the confirmed external resource.");
        var blockedCidrs = OutboundNetworkPolicy.ParseCidrs(await db.AgentRuntimeGlobalSettings.AsNoTracking()
            .Select(x => x.BlockedNetworkCidrs).SingleOrDefaultAsync(cancellationToken));
        var opened = await mediaAssets.OpenReadAsync(input.MediaAssetId, organizationId, cancellationToken)
            ?? throw new InvalidOperationException("The organization media asset was not found.");
        await using var assetContent = opened.Content;
        var asset = opened.Asset;
        if (asset.SizeBytes <= 0) throw new InvalidOperationException("Empty media assets cannot be transferred.");

        var bindingHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            input.MediaAssetId, input.Connection, input.BoundResourceId, input.InitiationUrl, metadata, asset.Sha256
        }, JsonOptions)))).ToLowerInvariant();
        var state = await db.PluginOperationalStates.SingleOrDefaultAsync(x =>
            x.AgentInstallationId == installationId && x.Kind == "media-transfer" &&
            x.ExternalKey == input.IdempotencyKey, cancellationToken);
        var progress = state is null ? null : JsonSerializer.Deserialize<TransferState>(state.PayloadJson, JsonOptions);
        if (progress is not null && !string.Equals(progress.BindingHash, bindingHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The media transfer idempotency key is bound to different content.");
        if (progress?.Status == "Completed")
            return Success(request.RequestId, new MediaTransferOutput("Completed", progress.ExternalResourceId, progress.Result));

        state ??= new PluginOperationalState
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = installationId,
            Kind = "media-transfer", ExternalKey = input.IdempotencyKey, CreatedAt = DateTimeOffset.UtcNow
        };
        if (db.Entry(state).State == EntityState.Detached) db.PluginOperationalStates.Add(state);
        var uploadKey = $"media-transfer.{state.Id:N}.upload-url";
        var uploadUrl = await secrets.GetAsync(installationId, uploadKey, cancellationToken);
        var token = await tokenBroker.GetAccessTokenAsync(installationId, connection, cancellationToken)
            ?? throw new InvalidOperationException("The OAuth connection must be reauthorized.");
        if (string.IsNullOrWhiteSpace(uploadUrl))
        {
            uploadUrl = await BeginAsync(initiationUri, token, metadata, asset.ContentType, asset.SizeBytes,
                declaration, blockedCidrs, cancellationToken);
            await secrets.SetAsync(installationId, uploadKey, uploadUrl, cancellationToken);
            progress = new TransferState(bindingHash, "Uploading", 0, null, null);
            await SaveStateAsync(state, progress, cancellationToken);
        }

        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out var uploadUri) ||
            !OutboundNetworkPolicy.IsAllowedOrigin(uploadUri, declaration.AllowedOrigins))
            throw new InvalidOperationException("The provider returned an unapproved upload destination.");
        var offset = Math.Clamp(progress?.Offset ?? 0, 0, asset.SizeBytes);
        if (assetContent.CanSeek) assetContent.Seek(offset, SeekOrigin.Begin);
        else await SkipAsync(assetContent, offset, cancellationToken);
        JsonElement? final = null;
        while (offset < asset.SizeBytes)
        {
            var count = (int)Math.Min(ChunkSize, asset.SizeBytes - offset);
            var buffer = new byte[count];
            await assetContent.ReadExactlyAsync(buffer, cancellationToken);
            using var response = await SendChunkWithRetryAsync(uploadUri, token, asset.ContentType, buffer,
                offset, asset.SizeBytes, blockedCidrs, cancellationToken);
            if ((int)response.StatusCode == 308)
            {
                offset = ReadProviderOffset(response, offset + count);
                if (assetContent.CanSeek) assetContent.Seek(offset, SeekOrigin.Begin);
                progress = progress! with { Offset = offset };
                await SaveStateAsync(state, progress, cancellationToken);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"The provider rejected an upload chunk with status {(int)response.StatusCode}.");
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > MaximumResultBytes) throw new InvalidOperationException("The provider upload result is too large.");
            final = bytes.Length == 0 ? null : JsonDocument.Parse(bytes).RootElement.Clone();
            offset = asset.SizeBytes;
        }
        var externalId = final.HasValue && final.Value.TryGetProperty("id", out var id) ? id.GetString() : null;
        progress = new TransferState(bindingHash, "Completed", asset.SizeBytes, externalId, final);
        await SaveStateAsync(state, progress, cancellationToken);
        await secrets.RemoveAsync(installationId, uploadKey, cancellationToken);
        await audit.WriteAsync("plugin.media-transfer.completed", "PluginInstallation", installationId,
            $"Transferred organization media asset {asset.Id} through an opaque provider upload session.",
            JsonSerializer.Serialize(new { organizationId, installationId, assetId = asset.Id, asset.SizeBytes,
                asset.Sha256, input.Connection, input.BoundResourceId, input.IdempotencyKey, externalId }), cancellationToken);
        return Success(request.RequestId, new MediaTransferOutput("Completed", externalId, final));
    }

    private static bool IsWithin(Uri uri, PluginWebAccessRule rule) =>
        uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && !uri.IsLoopback &&
        string.Equals(OutboundNetworkPolicy.NormalizeHost(rule.Host),
            OutboundNetworkPolicy.NormalizeHost(uri.DnsSafeHost), StringComparison.Ordinal) &&
        (rule.Port is null || rule.Port == uri.Port) &&
        OutboundNetworkPolicy.IsPathWithinPrefix(uri.AbsolutePath, rule.PathPrefix);

    private static async Task<string> BeginAsync(Uri uri, string token, string metadata, string contentType,
        long size, PluginConnectionDeclaration declaration, IReadOnlyList<OutboundNetworkPolicy.CidrRange> blockedCidrs,
        CancellationToken cancellationToken)
    {
        using var client = await CreateClientAsync(uri, blockedCidrs, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", contentType);
        request.Content = new StringContent(metadata, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode || response.Headers.Location is not { } location)
            throw new InvalidOperationException($"The provider rejected the resumable upload request with status {(int)response.StatusCode}.");
        var absolute = location.IsAbsoluteUri ? location : new Uri(uri, location);
        if (!OutboundNetworkPolicy.IsAllowedOrigin(absolute, declaration.AllowedOrigins))
            throw new InvalidOperationException("The provider returned an unapproved upload destination.");
        return absolute.AbsoluteUri;
    }

    private static async Task<HttpResponseMessage> SendChunkWithRetryAsync(Uri uri, string token,
        string contentType, byte[] body, long offset, long total,
        IReadOnlyList<OutboundNetworkPolicy.CidrRange> blockedCidrs, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var client = await CreateClientAsync(uri, blockedCidrs, cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Put, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + body.Length - 1, total);
                return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            }
            catch (Exception exception) when (attempt < 2 && exception is HttpRequestException or IOException or SocketException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
            }
        }
    }

    private static long ReadProviderOffset(HttpResponseMessage response, long fallback)
    {
        if (!response.Headers.TryGetValues("Range", out var values)) return fallback;
        var value = values.FirstOrDefault();
        var dash = value?.LastIndexOf('-') ?? -1;
        return dash >= 0 && long.TryParse(value![(dash + 1)..], out var end) ? end + 1 : fallback;
    }

    private static async Task<HttpClient> CreateClientAsync(Uri uri,
        IReadOnlyList<OutboundNetworkPolicy.CidrRange> blockedCidrs, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(x => OutboundNetworkPolicy.IsForbiddenAddress(x, blockedCidrs)))
            throw new InvalidOperationException("The upload destination resolves to a private or reserved address.");
        var address = addresses[0];
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10), MaxConnectionsPerServer = 1,
            ConnectCallback = async (_, token) =>
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
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(5) };
    }

    private static async Task SkipAsync(Stream stream, long bytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        while (bytes > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, bytes)), cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            bytes -= read;
        }
    }

    private async Task SaveStateAsync(PluginOperationalState state, TransferState value,
        CancellationToken cancellationToken)
    {
        state.PayloadJson = JsonSerializer.Serialize(value, JsonOptions);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static CapabilityResult Success(string requestId, MediaTransferOutput output) => new()
    {
        RequestId = requestId, Succeeded = true, ContentType = "application/json",
        Payload = JsonPayload.From(output, JsonOptions)
    };

    private static CapabilityResult Failure(string requestId, string error) => new()
    {
        RequestId = requestId, Succeeded = false, ContentType = "application/json", Error = error,
        Payload = JsonPayload.FromUtf8("{\"isError\":true}")
    };

    private sealed record MediaTransferInput(Guid MediaAssetId, string Connection, string BoundResourceId,
        string InitiationUrl, JsonElement? Metadata, string IdempotencyKey);
    private sealed record MediaTransferOutput(string Status, string? ExternalResourceId, JsonElement? Result);
    private sealed record TransferState(string BindingHash, string Status, long Offset,
        string? ExternalResourceId, JsonElement? Result);
}
