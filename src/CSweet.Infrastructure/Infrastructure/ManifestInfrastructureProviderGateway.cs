using System.Net.Http.Headers;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CSweet.Agent.SDK;
using CSweet.Application.Infrastructure;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Renci.SshNet;

namespace CSweet.Infrastructure.Infrastructure;

public sealed class ManifestInfrastructureProviderGateway(
    CSweetDbContext db,
    IPluginSecretStore secrets,
    IPluginOAuthTokenBroker oauth,
    IHttpClientFactory httpClients,
    IConfiguration configuration,
    IAuditEventWriter audit) : IInfrastructureProviderGateway
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumProviderResponseBytes = 4 * 1024 * 1024;

    public async Task<JsonElement> InvokeAsync(Guid organizationId, Guid installationId, string capability,
        JsonElement input, CancellationToken cancellationToken = default)
    {
        RequireFeature("ProviderGateway");
        var (installation, manifest) = await LoadAsync(organizationId, installationId, cancellationToken);
        var mcp = manifest.McpServers.SelectMany(server => server.Tools.Select(tool => (server, tool)))
            .SingleOrDefault(x => x.tool.Capability == capability);
        JsonElement result;
        string transport;
        if (mcp.tool is not null)
        {
            result = await InvokeMcpAsync(organizationId, installation, mcp.server, mcp.tool, input, cancellationToken);
            transport = "mcp";
        }
        else
        {
            var operation = manifest.ProviderOperations.SingleOrDefault(x => x.Capability == capability)
                ?? throw new InvalidOperationException("The requested provider capability is not declared by this installation.");
            if (!operation.Effect.Equals("read", StringComparison.Ordinal))
                throw new InvalidOperationException("Provider writes may execute only from an approved infrastructure change set.");
            result = await InvokeLegacyApiAsync(installation, operation, input, cancellationToken);
            transport = "typed-provider-api";
        }
        await audit.WriteAsync("infrastructure.provider.read", nameof(AgentInstallation), installationId,
            $"Invoked declared provider read capability {capability}.",
            JsonSerializer.Serialize(new { organizationId, installationId, capability, transport }), cancellationToken);
        return result;
    }

    public async Task<JsonElement> InvokeApprovedAsync(Guid organizationId, Guid installationId,
        string capability, JsonElement input, CancellationToken cancellationToken)
    {
        RequireFeature("ProviderGateway");
        RequireFeature("InfrastructureGovernance");
        var (installation, manifest) = await LoadAsync(organizationId, installationId, cancellationToken);
        var mcp = manifest.McpServers.SelectMany(server => server.Tools.Select(tool => (server, tool)))
            .SingleOrDefault(x => x.tool.Capability == capability);
        if (mcp.tool is not null)
        {
            var write = await InvokeMcpAsync(organizationId, installation, mcp.server, mcp.tool, input, cancellationToken);
            var verificationName = mcp.tool.RemoteName switch
            {
                "dns_records_save" or "dns_records_delete" => "dns_records_get",
                "domain_set_contacts" or "domain_set_nameservers" => "domains_list",
                _ => null
            };
            if (mcp.tool.Effect == "read" || verificationName is null) return write;
            var verificationTool = mcp.server.Tools.SingleOrDefault(x => x.RemoteName == verificationName && x.Effect == "read")
                ?? throw new InvalidOperationException("The manifest does not declare the required read-after-write verification tool.");
            var verificationInput = verificationName == "dns_records_get" &&
                input.TryGetProperty("domainName", out var domainName)
                ? JsonSerializer.SerializeToElement(new { domainName = domainName.GetString(), take = 500 }, JsonOptions)
                : JsonSerializer.SerializeToElement(new { }, JsonOptions);
            var observed = await InvokeMcpAsync(organizationId, installation, mcp.server, verificationTool,
                verificationInput, cancellationToken);
            return JsonSerializer.SerializeToElement(new { write, observed, reconciledAt = DateTimeOffset.UtcNow }, JsonOptions);
        }
        var operation = manifest.ProviderOperations.SingleOrDefault(x => x.Capability == capability)
            ?? throw new InvalidOperationException("The approved provider capability is no longer declared by this installation.");
        var providerWrite = await InvokeLegacyApiAsync(installation, operation, input, cancellationToken);
        var verificationCommand = operation.Command switch
        {
            "namecheap.domains.create" => "namecheap.domains.getList",
            "namecheap.domains.dns.setHosts" => "namecheap.domains.dns.getHosts",
            _ => null
        };
        if (operation.Effect == "read" || verificationCommand is null) return providerWrite;
        var verifier = manifest.ProviderOperations.SingleOrDefault(x => x.Command == verificationCommand && x.Effect == "read")
            ?? throw new InvalidOperationException("The manifest does not declare the required provider reconciliation operation.");
        var providerVerificationInput = LegacyVerificationInput(verificationCommand, input);
        var observedProvider = await InvokeLegacyApiAsync(installation, verifier, providerVerificationInput, cancellationToken);
        return JsonSerializer.SerializeToElement(new { write = providerWrite, observed = observedProvider,
            reconciledAt = DateTimeOffset.UtcNow }, JsonOptions);
    }

    public async Task<InfrastructureFileTransferResponse> TransferAsync(Guid organizationId, Guid installationId,
        InfrastructureFileTransferRequest request, CancellationToken cancellationToken = default)
    {
        RequireFeature("RestrictedFileTransfer");
        var (_, manifest) = await LoadAsync(organizationId, installationId, cancellationToken);
        var target = manifest.FileTransferTargets.SingleOrDefault(x => x.Id == request.Target)
            ?? throw new InvalidOperationException("The requested file-transfer target is not declared.");
        if (!target.Operations.Contains(request.Operation, StringComparer.Ordinal))
            throw new InvalidOperationException("The requested file-transfer operation is not allowed.");
        if (request.Host.Length > 253 || !target.AllowedHostSuffixes.Any(suffix =>
                request.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The SFTP host is outside the manifest allowlist.");
        var relative = NormalizeRelativePath(request.RelativePath);
        var credentialJson = await secrets.GetAsync(installationId,
            PluginSetupService.ConfigurationSecretKey(target.Credential), cancellationToken)
            ?? throw new InvalidOperationException("The SFTP credential is not configured.");
        var credential = JsonSerializer.Deserialize<SftpCredential>(credentialJson, JsonOptions)
            ?? throw new InvalidOperationException("The SFTP credential is invalid.");
        if (!string.Equals(credential.Host, request.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The SFTP request does not match the exact approved host.");
        if (string.IsNullOrWhiteSpace(credential.HostKeyFingerprint))
            throw new InvalidOperationException("An administrator-approved SSH host-key fingerprint is required.");

        var root = target.RootPath.Trim('/', '\\');
        var remotePath = string.IsNullOrEmpty(relative) ? root : $"{root}/{relative}";
        string? observedFingerprint = null;
        var connection = new ConnectionInfo(request.Host, target.Port, credential.Username,
            new PasswordAuthenticationMethod(credential.Username, credential.Password));
        using var client = new SftpClient(connection);
        client.HostKeyReceived += (_, args) =>
        {
            observedFingerprint = Convert.ToHexString(SHA256.HashData(args.HostKey)).ToLowerInvariant();
            args.CanTrust = FixedEquals(observedFingerprint, NormalizeFingerprint(credential.HostKeyFingerprint));
        };
        try
        {
            await Task.Run(() => client.Connect(), cancellationToken);
            InfrastructureFileTransferResponse response = request.Operation switch
            {
                "probe" => new("Verified", request.Host, relative, null, null, observedFingerprint),
                "list" => await ListAsync(client, request.Host, remotePath, relative, observedFingerprint, cancellationToken),
                "stat" => await StatAsync(client, request.Host, remotePath, relative, observedFingerprint, cancellationToken),
                "upload" => await UploadAsync(client, request, remotePath, relative, observedFingerprint, cancellationToken),
                _ => throw new InvalidOperationException("The file-transfer operation is unsupported.")
            };
            await audit.WriteAsync("infrastructure.file-transfer", nameof(AgentInstallation), installationId,
                $"Completed confined SFTP {request.Operation} for {request.Host}.",
                JsonSerializer.Serialize(new { organizationId, installationId, request.Target, request.Operation,
                    request.Host, relativePath = relative, response.Length, response.ContentHash }), cancellationToken);
            return response;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (observedFingerprint is not null &&
                !FixedEquals(observedFingerprint, NormalizeFingerprint(credential.HostKeyFingerprint)))
                throw new InvalidOperationException("The SSH host key changed. Renewed administrator confirmation is required.");
            throw new InvalidOperationException("The restricted SFTP operation failed.");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    private async Task<JsonElement> InvokeMcpAsync(Guid organizationId, AgentInstallation installation, PluginMcpServerDeclaration server,
        PluginMcpToolDeclaration tool, JsonElement input, CancellationToken token)
    {
        var connection = await db.PluginConnections.AsNoTracking().SingleOrDefaultAsync(x =>
            x.AgentInstallationId == installation.Id && x.DeclarationId == server.Connection &&
            x.Status == PluginConnectionStatus.Connected, token)
            ?? throw new InvalidOperationException("The declared MCP connection is not connected.");
        var accessToken = await oauth.GetAccessTokenAsync(installation.Id, connection, token)
            ?? throw new InvalidOperationException("The Namecheap connection must be reauthorized.");
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", server.ProtocolVersions.Last());
        request.Content = JsonContent.Create(new
        {
            jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method = "tools/call",
            @params = new { name = tool.RemoteName, arguments = input }
        });
        using var response = await httpClients.CreateClient(nameof(ManifestInfrastructureProviderGateway))
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("The Namecheap MCP request failed.");
        var body = await ReadBoundedAsync(response.Content, token);
        using var document = JsonDocument.Parse(ExtractMcpJson(body));
        if (document.RootElement.TryGetProperty("error", out _))
            throw new InvalidOperationException("The Namecheap MCP operation returned an error.");
        var result = document.RootElement.GetProperty("result");
        if (result.TryGetProperty("structuredContent", out var structured))
            return await ProtectProviderResultAsync(organizationId, installation.Id, structured, token);
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var text = content.EnumerateArray().Select(x => x.TryGetProperty("text", out var value) ? value.GetString() : null)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (text is not null)
                try { using var parsed = JsonDocument.Parse(text); return await ProtectProviderResultAsync(organizationId, installation.Id, parsed.RootElement, token); }
                catch (JsonException) { }
        }
        return await ProtectProviderResultAsync(organizationId, installation.Id, result, token);
    }

    private async Task<JsonElement> InvokeLegacyApiAsync(AgentInstallation installation,
        PluginProviderOperationDeclaration operation, JsonElement input, CancellationToken token)
    {
        var enabled = configuration.GetValue<bool>("Infrastructure:Namecheap:ProductionApiEnabled");
        if (operation.ProviderProfile == "com.csweet.public-site-verifier")
            return await VerifyPublicSiteAsync(operation, input, token);
        if (operation.ProviderProfile == "com.namecheap.hosted-checkout")
        {
            var domain = input.TryGetProperty("domain", out var domainNode) ? domainNode.GetString() : null;
            var plan = input.TryGetProperty("plan", out var planNode) ? planNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(plan))
                throw new InvalidOperationException("The approved hosting checkout requires a domain and plan.");
            return JsonSerializer.SerializeToElement(new
            {
                status = "consent_required", domain, plan,
                checkoutUrl = operation.ProductionEndpoint,
                note = "Complete payment, MFA, and anti-bot checks directly with Namecheap, then return to C-Sweet."
            }, JsonOptions);
        }
        var sandbox = input.TryGetProperty("sandbox", out var sandboxValue) && sandboxValue.ValueKind == JsonValueKind.True;
        if (!sandbox && !enabled)
            throw new InvalidOperationException("Namecheap production API access requires administrator enablement and fixed-egress IPv4 whitelisting.");
        var secretJson = await secrets.GetAsync(installation.Id,
            PluginSetupService.ConfigurationSecretKey(operation.Credential), token)
            ?? throw new InvalidOperationException("The legacy provider credential is not configured.");
        var credential = JsonSerializer.Deserialize<NamecheapApiCredential>(secretJson, JsonOptions)
            ?? throw new InvalidOperationException("The legacy provider credential is invalid.");
        var fixedEgressIPv4 = configuration["Infrastructure:Namecheap:FixedEgressIPv4"];
        if (!IPAddress.TryParse(fixedEgressIPv4, out var egressAddress) ||
            egressAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || IsPrivateOrReserved(egressAddress))
            throw new InvalidOperationException("Namecheap legacy API access requires a platform-configured public fixed-egress IPv4 address.");
        var endpoint = sandbox ? operation.SandboxEndpoint : operation.ProductionEndpoint;
        if (endpoint is null) throw new InvalidOperationException("This provider operation has no sandbox endpoint.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ApiUser"] = credential.ApiUser, ["ApiKey"] = credential.ApiKey,
            ["UserName"] = credential.UserName, ["ClientIp"] = fixedEgressIPv4,
            ["Command"] = operation.Command
        };
        foreach (var property in input.EnumerateObject())
        {
            if (property.NameEquals("sandbox")) continue;
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                throw new InvalidOperationException("Legacy API inputs must contain only declared scalar parameters.");
            values[property.Name] = property.Value.ToString();
        }
        using var content = new FormUrlEncodedContent(values);
        using var response = await httpClients.CreateClient(nameof(ManifestInfrastructureProviderGateway))
            .PostAsync(endpoint, content, token);
        var bytes = await ReadBoundedAsync(response.Content, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("The Namecheap API request failed.");
        XDocument xml;
        try { xml = XDocument.Parse(Encoding.UTF8.GetString(bytes), LoadOptions.None); }
        catch { throw new InvalidOperationException("The Namecheap API returned malformed XML."); }
        var root = xml.Root ?? throw new InvalidOperationException("The Namecheap API returned an empty response.");
        if (string.Equals(root.Attribute("Status")?.Value, "ERROR", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Namecheap API rejected the request.");
        return JsonSerializer.SerializeToElement(XmlToSafeObject(root), JsonOptions);
    }

    private async Task<(AgentInstallation Installation, PluginManifest Manifest)> LoadAsync(Guid organizationId,
        Guid installationId, CancellationToken token)
    {
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion)
            .SingleOrDefaultAsync(x => x.Id == installationId && x.BusinessId == organizationId.ToString("D") && x.IsEnabled, token)
            ?? throw new InvalidOperationException("The infrastructure installation is unavailable.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion!.ManifestJson, JsonOptions)
            ?? throw new InvalidOperationException("The installed infrastructure manifest is invalid.");
        return (installation, manifest);
    }

    private async Task<JsonElement> VerifyPublicSiteAsync(PluginProviderOperationDeclaration operation,
        JsonElement input, CancellationToken token)
    {
        var domain = input.TryGetProperty("domain", out var domainNode) ? domainNode.GetString() : null;
        var marker = input.TryGetProperty("expectedMarker", out var markerNode) ? markerNode.GetString() : null;
        if (string.IsNullOrWhiteSpace(domain) || Uri.CheckHostName(domain) != UriHostNameType.Dns ||
            string.IsNullOrWhiteSpace(marker) || marker.Length > 256)
            throw new InvalidOperationException("Public HTTPS verification requires a DNS domain and bounded marker.");
        var attempts = new List<object>();
        var anyResolved = false;
        var anyHttps = false;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(domain, token);
                var resolves = addresses.Length > 0 && addresses.All(address => !IsPrivateOrReserved(address));
                anyResolved |= resolves;
                if (!resolves)
                {
                    attempts.Add(new { attempt, resolves = false, httpsValid = false, markerMatched = false });
                }
                else
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{domain}/"));
                    using var response = await httpClients.CreateClient(nameof(ManifestInfrastructureProviderGateway))
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    var body = await ReadBoundedAsync(response.Content, token);
                    var found = response.IsSuccessStatusCode &&
                        Encoding.UTF8.GetString(body).Contains(marker, StringComparison.Ordinal);
                    anyHttps |= response.IsSuccessStatusCode;
                    attempts.Add(new { attempt, resolves = true, httpsValid = response.IsSuccessStatusCode,
                        markerMatched = found, statusCode = (int?)response.StatusCode });
                    if (response.IsSuccessStatusCode && found)
                        return JsonSerializer.SerializeToElement(new { domain, resolves = true, httpsValid = true,
                            markerMatched = true, attempts, verifiedAt = DateTimeOffset.UtcNow }, JsonOptions);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or System.Net.Sockets.SocketException)
            {
                attempts.Add(new { attempt, resolves = false, httpsValid = false, markerMatched = false,
                    failure = "dns_or_tls_not_ready" });
            }
            if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        return JsonSerializer.SerializeToElement(new { domain, resolves = anyResolved, httpsValid = anyHttps,
            markerMatched = false, attempts, verifiedAt = DateTimeOffset.UtcNow }, JsonOptions);
    }

    private static bool IsPrivateOrReserved(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None)) return true;
        var bytes = address.MapToIPv4().GetAddressBytes();
        return bytes[0] is 0 or 10 or 127 || bytes[0] == 169 && bytes[1] == 254 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] >= 224;
    }

    private void RequireFeature(string name)
    {
        if (!configuration.GetValue<bool>($"FeatureFlags:{name}"))
            throw new InvalidOperationException($"The {name} feature is not enabled.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        await using var input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (output.Length < MaximumProviderResponseBytes)
        {
            var read = await input.ReadAsync(buffer, token);
            if (read == 0) return output.ToArray();
            if (output.Length + read > MaximumProviderResponseBytes)
                throw new InvalidOperationException("The provider response exceeded the platform limit.");
            output.Write(buffer, 0, read);
        }
        throw new InvalidOperationException("The provider response exceeded the platform limit.");
    }

    private static byte[] ExtractMcpJson(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body).Trim();
        if (!text.StartsWith("data:", StringComparison.Ordinal)) return body;
        var data = text.Split('\n').Where(x => x.StartsWith("data:", StringComparison.Ordinal))
            .Select(x => x[5..].Trim()).LastOrDefault(x => x.Length > 0);
        return data is null ? body : Encoding.UTF8.GetBytes(data);
    }

    private async Task<JsonElement> ProtectProviderResultAsync(Guid organizationId, Guid installationId,
        JsonElement element, CancellationToken token)
    {
        var node = JsonNode.Parse(element.GetRawText());
        await ProtectCheckoutLinksAsync(organizationId, installationId, node, token);
        RedactNode(node);
        return JsonSerializer.SerializeToElement(node, JsonOptions);
    }

    private async Task ProtectCheckoutLinksAsync(Guid organizationId, Guid installationId,
        JsonNode? node, CancellationToken token)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                if ((key.Equals("consentUrl", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("checkoutUrl", StringComparison.OrdinalIgnoreCase)) &&
                    obj[key] is JsonValue value && value.TryGetValue<string>(out var raw) &&
                    Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
                    (uri.Host.Equals("namecheap.com", StringComparison.OrdinalIgnoreCase) ||
                     uri.Host.EndsWith(".namecheap.com", StringComparison.OrdinalIgnoreCase)))
                {
                    var actionId = Guid.NewGuid();
                    var now = DateTimeOffset.UtcNow;
                    var lifetimeSeconds = obj["expiresInSeconds"] is JsonValue lifetime &&
                        lifetime.TryGetValue<int>(out var declaredLifetime)
                        ? Math.Clamp(declaredLifetime, 60, 1800) : 1200;
                    var expiresAt = now.AddSeconds(lifetimeSeconds);
                    await secrets.SetAsync(installationId, $"infrastructure.checkout-action.{actionId:N}", raw, token);
                    db.PluginOperationalStates.Add(new PluginOperationalState
                    {
                        Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = installationId,
                        Kind = "infrastructure.checkout-action", ExternalKey = actionId.ToString("N"), Revision = 1,
                        PayloadJson = JsonSerializer.Serialize(new { expiresAt }, JsonOptions),
                        CreatedAt = now, UpdatedAt = now
                    });
                    await db.SaveChangesAsync(token);
                    obj.Remove(key);
                    obj["checkoutAction"] = JsonSerializer.SerializeToNode(new
                    {
                        id = actionId,
                        uri = $"/api/core/organizations/{organizationId:D}/infrastructure/checkout-actions/{actionId:D}",
                        expiresAt
                    }, JsonOptions);
                    continue;
                }
                await ProtectCheckoutLinksAsync(organizationId, installationId, obj[key], token);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) await ProtectCheckoutLinksAsync(organizationId, installationId, child, token);
    }

    internal static void RedactNode(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(x => x.Key).ToArray())
                if (key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("apiKey", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("contactDetails", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("addressSuggestion", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("consentUrl", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("checkoutUrl", StringComparison.OrdinalIgnoreCase)) obj[key] = "[REDACTED]";
                else if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && ContainsCheckoutLink(text))
                    obj[key] = "[CHECKOUT LINK WITHHELD BY C-SWEET]";
                else RedactNode(obj[key]);
        else if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++)
                if (array[index] is JsonValue value && value.TryGetValue<string>(out var text) && ContainsCheckoutLink(text))
                    array[index] = "[CHECKOUT LINK WITHHELD BY C-SWEET]";
                else RedactNode(array[index]);
    }

    private static bool ContainsCheckoutLink(string value) =>
        value.Contains("namecheap.com/apps/consent/", StringComparison.OrdinalIgnoreCase);

    private static object XmlToSafeObject(XElement element) => new
    {
        name = element.Name.LocalName,
        attributes = element.Attributes().Where(x => !IsSensitive(x.Name.LocalName))
            .ToDictionary(x => x.Name.LocalName, x => x.Value, StringComparer.OrdinalIgnoreCase),
        value = element.HasElements ? null : element.Value,
        children = element.Elements().Select(XmlToSafeObject).ToArray()
    };

    private static bool IsSensitive(string name) => name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("apikey", StringComparison.OrdinalIgnoreCase);

    private static JsonElement LegacyVerificationInput(string command, JsonElement original)
    {
        var values = new JsonObject();
        if (original.TryGetProperty("sandbox", out var sandbox)) values["sandbox"] = sandbox.GetBoolean();
        if (command == "namecheap.domains.dns.getHosts")
            foreach (var name in new[] { "SLD", "TLD" })
                if (original.TryGetProperty(name, out var value)) values[name] = value.GetString();
        return JsonSerializer.SerializeToElement(values, JsonOptions);
    }

    private static string NormalizeRelativePath(string value)
    {
        var normalized = (value ?? string.Empty).Replace('\\', '/').Trim('/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(x => x is "." or "..") ||
            normalized.Contains(':') || Path.IsPathRooted(normalized))
            throw new InvalidOperationException("The SFTP path escapes the approved root.");
        return normalized;
    }

    private static async Task<InfrastructureFileTransferResponse> ListAsync(SftpClient client, string host,
        string remotePath, string relative, string? fingerprint, CancellationToken token)
    {
        var entries = await Task.Run(() => client.ListDirectory(remotePath).Where(x => x.Name is not ("." or "..")).ToArray(), token);
        return new("Listed", host, relative, entries.LongLength, null, fingerprint);
    }

    private static async Task<InfrastructureFileTransferResponse> StatAsync(SftpClient client, string host,
        string remotePath, string relative, string? fingerprint, CancellationToken token)
    {
        var attributes = await Task.Run(() => client.GetAttributes(remotePath), token);
        return new("Found", host, relative, attributes.Size, null, fingerprint);
    }

    private static async Task<InfrastructureFileTransferResponse> UploadAsync(SftpClient client,
        InfrastructureFileTransferRequest request, string remotePath, string relative, string? fingerprint,
        CancellationToken token)
    {
        var content = request.Content ?? throw new InvalidOperationException("Upload content is required.");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(request.ExpectedContentHash) || !FixedEquals(hash, request.ExpectedContentHash))
            throw new InvalidOperationException("The upload bytes do not match the approved content hash.");
        await using var stream = new MemoryStream(content, writable: false);
        await Task.Run(() => client.UploadFile(stream, remotePath, true), token);
        return new("Uploaded", request.Host, relative, content.LongLength, hash, fingerprint);
    }

    private static string NormalizeFingerprint(string value) => value.Replace(":", string.Empty).Trim().ToLowerInvariant();
    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.ASCII.GetBytes(left.ToLowerInvariant());
        var b = Encoding.ASCII.GetBytes(right.ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private sealed record NamecheapApiCredential(string ApiUser, string ApiKey, string UserName);
    private sealed record SftpCredential(string Host, string Username, string Password, string HostKeyFingerprint);
}
