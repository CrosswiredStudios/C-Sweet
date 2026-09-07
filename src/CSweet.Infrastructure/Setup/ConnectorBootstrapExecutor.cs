using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Host-only read-only setup execution; no connector runtime or model is started.</summary>
public sealed class ConnectorBootstrapExecutor(CSweetDbContext db, IConnectorHttpTransport transport)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<JsonElement> ExecuteAsync(Guid organizationId, Guid installationId, string stepId,
        JsonElement input, CancellationToken ct)
    {
        var authority = await ResolveAsync(organizationId, installationId, stepId, ct);
        RequestSchemaValidator.ValidateSchema(authority.Operation.InputSchema);
        RequestSchemaValidator.Validate(input, authority.Operation.InputSchema);
        var request = ConnectorRequestMaterializer.Prepare(authority.Operation, input, authority.ResourceId ?? "");
        if (request.Method != "GET" || request.Effect != "read" || request.MediaAssetId is not null ||
            request.ResourceChecks.Count > 0 || request.SecretResponseFields.Count > 0)
            throw new UnauthorizedAccessException("Setup may only perform the declared account-discovery read.");
        var frozen = Fingerprint(authority, request);
        async Task Revalidate(CancellationToken token)
        {
            var current = await ResolveAsync(organizationId, installationId, stepId, token);
            if (Fingerprint(current, ConnectorRequestMaterializer.Prepare(current.Operation, input, current.ResourceId ?? "")) != frozen)
                throw new UnauthorizedAccessException("Setup authority changed during the provider read.");
        }
        await Revalidate(ct);
        var response = await transport.SendAsync(installationId, authority.ConnectionId, request, Revalidate, ct);
        if (response.StatusCode is < 200 or >= 300)
            throw new InvalidOperationException($"The provider could not validate account access (HTTP {response.StatusCode}). Reconnect or try again later.");
        using var document = JsonDocument.Parse(response.Body, new JsonDocumentOptions { MaxDepth = 32 });
        _ = ConnectorRequestMaterializer.Canonical(document.RootElement);
        RequestSchemaValidator.ValidateSchema(authority.Operation.OutputSchema);
        RequestSchemaValidator.Validate(document.RootElement, authority.Operation.OutputSchema);
        var choices = ConnectorAccountProjection.Read(document.RootElement, authority.Step.AccountOptions!);
        await Revalidate(ct);
        if (authority.Step.Kind == "account-selector")
            return JsonSerializer.SerializeToElement(new { accounts = choices }, JsonOptions);
        if (authority.ResourceId is null || !choices.Any(x => x.Id == authority.ResourceId))
            throw new UnauthorizedAccessException("The confirmed account is no longer accessible to this authorization.");
        return JsonSerializer.SerializeToElement(new { healthy = true, message = "The confirmed account is accessible." }, JsonOptions);
    }

    private static string Fingerprint(Authority authority, ConnectorPreparedRequest request) =>
        ConnectorRequestMaterializer.Hash(JsonSerializer.SerializeToElement(new
        { authority.Digest, authority.ConnectionId, authority.GrantRevision, authority.GrantedScopes, authority.ResourceId, request }, JsonOptions));

    private async Task<Authority> ResolveAsync(Guid organizationId, Guid installationId, string stepId, CancellationToken ct)
    {
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).Include(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == installationId && x.BusinessId == organizationId.ToString() &&
                x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, ct)
            ?? throw new UnauthorizedAccessException("The connector installation is not available in this organization.");
        if (installation.PackageVersion?.PluginKind != PluginKind.Connector || installation.Grant is null ||
            installation.SetupState == PluginSetupState.Ready || installation.SetupStepId != stepId ||
            string.IsNullOrWhiteSpace(installation.PackageVersion.PackageDigest))
            throw new UnauthorizedAccessException("Only the current connector setup step can execute.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(installation.PackageVersion.ManifestJson, JsonOptions)!;
        PluginManifestReader.ValidateConnectorContracts(manifest);
        var step = manifest.Setup?.Flows.SingleOrDefault(x => x.Id == installation.SetupFlowId)?.Steps.SingleOrDefault(x => x.Id == stepId)
            ?? throw new UnauthorizedAccessException("The current setup flow is not declared.");
        if (step.AccountOptions is null || step.Kind is not ("account-selector" or "health-check"))
            throw new UnauthorizedAccessException("This setup step is not an account discovery or validation read.");
        var operation = manifest.ProviderOperations.SingleOrDefault(x => x.Capability == step.Capability && x.Http?.Bootstrap == true)
            ?? throw new UnauthorizedAccessException("The setup operation is not declared.");
        var grants = JsonSerializer.Deserialize<string[]>(installation.Grant.ProvidedCapabilitiesJson, JsonOptions) ?? [];
        if (!grants.Contains(operation.Capability, StringComparer.Ordinal) ||
            !manifest.Provides.Any(x => x.Name == operation.Capability && x.RiskClass == "bootstrap"))
            throw new UnauthorizedAccessException("The setup operation is not approved.");
        var declaration = manifest.Connections.Single(x => x.Id == operation.Http!.Connection);
        var connection = await db.PluginConnections.AsNoTracking().SingleOrDefaultAsync(x =>
            x.AgentInstallationId == installationId && x.DeclarationId == declaration.Id &&
            x.Status == PluginConnectionStatus.Connected, ct)
            ?? throw new UnauthorizedAccessException("Connect the provider before account discovery.");
        var granted = JsonSerializer.Deserialize<string[]>(connection.GrantedScopesJson, JsonOptions) ?? [];
        var required = operation.Http!.ScopeSets.SelectMany(id => declaration.ScopeSets.Single(x => x.Id == id).Scopes);
        if (connection.ProviderProfile != declaration.ProviderProfile || required.Except(granted, StringComparer.Ordinal).Any() ||
            !await db.ConnectorProfileApprovals.AsNoTracking().AnyAsync(x => x.ConnectorInstallationId == installationId &&
                x.PackageDigest == installation.PackageVersion.PackageDigest && x.ProfileId == declaration.ProviderProfile && x.RevokedAt == null, ct))
            throw new UnauthorizedAccessException("The connector build, provider profile or permissions are not approved.");
        return new(installation.PackageVersion.PackageDigest, installation.Grant.GrantRevision,
            connection.Id, connection.GrantedScopesJson, connection.BoundResourceId, step, operation);
    }
    private sealed record Authority(string Digest, long GrantRevision, Guid ConnectionId, string GrantedScopes,
        string? ResourceId, PluginSetupStep Step, PluginProviderOperationDeclaration Operation);
}
