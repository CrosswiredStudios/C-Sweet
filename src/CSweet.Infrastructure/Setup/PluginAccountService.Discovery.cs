using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class PluginAccountService
{
    public async Task ProcessPendingAsync(CancellationToken ct)
    {
        await ProcessRevocations(ct);
        await ProcessDeletions(ct);
        await FinalizeRemovals(ct);
        await QueueMaintenance(ct);
        var abandoned = await db.PluginAccountSetupSessions.Where(s =>
            (s.Status == "AwaitingConsent" && s.ExpiresAt <= DateTimeOffset.UtcNow ||
             s.Status == "Cancelled" || s.Status == "Expired" || s.Status == "Failed") &&
            s.FailureCode != "expired_cleaned").Take(25).ToListAsync(ct);
        foreach (var attempt in abandoned)
        {
            var managedJson = await secrets.GetAsync(attempt.PluginInstallationId, ManagedContextKey(attempt.ConnectionId), ct);
            if (managedJson is not null)
            {
                if (managed is null) continue;
                try
                {
                    var cancelled = await managed.CancelAttempt(attempt.Id, ct);
                    if (cancelled.ConnectionId is { } remote && (await managed.Snapshot(remote, ct)).Status != "Revoked") continue;
                    await secrets.RemoveAsync(attempt.PluginInstallationId, ManagedContextKey(attempt.ConnectionId), ct);
                    var pendingConnection = await db.PluginConnections.SingleAsync(x => x.Id == attempt.ConnectionId, ct);
                    pendingConnection.Status = PluginConnectionStatus.Revoked; pendingConnection.AuthorizationRevision++;
                }
                catch (Exception error) when (error is HttpRequestException or InvalidOperationException) { continue; }
            }
            // Removal is repeatable: a crash before the state commit cannot leave a reusable verifier.
            await secrets.RemoveAsync(attempt.PluginInstallationId, VerifierKey(attempt.Id), ct);
            if (attempt.Status == "AwaitingConsent") attempt.Status = "Expired";
            attempt.FailureCode = "expired_cleaned"; attempt.Revision++;
            await db.SaveChangesAsync(ct);
        }
        var retired = await db.PluginAccountSetupSessions.Where(s => (s.Status == "Ready" || s.Status == "NoResources") && s.RetiredCredentialKey != null).Take(25).ToListAsync(ct);
        foreach (var completed in retired)
        {
            if (completed.RetiredCredentialKey!.StartsWith("managed.context.", StringComparison.Ordinal))
            {
                if (!await RetireManaged(completed, ct)) continue;
            }
            await secrets.RemoveAsync(completed.PluginInstallationId, completed.RetiredCredentialKey!, ct);
            completed.RetiredCredentialKey = null; completed.Revision++; await db.SaveChangesAsync(ct);
        }
        var ids = await db.PluginAccountSetupSessions.AsNoTracking().Where(s => s.Status == "Importing" &&
            (s.LeaseUntil == null || s.LeaseUntil < DateTimeOffset.UtcNow)).OrderBy(s => s.CreatedAt).Select(s => s.Id).Take(10).ToArrayAsync(ct);
        foreach (var id in ids) await ImportAsync(id, ct);
    }
    public async Task RetryImportAsync(Guid userId, Guid installationId, Guid setupId, CancellationToken ct)
    {
        var session = await db.PluginAccountSetupSessions.SingleAsync(x => x.Id == setupId && x.PluginInstallationId == installationId && x.ApplicationUserId == userId, ct);
        await Current(session, ct);
        if (session.Status != "ImportFailed") throw new InvalidOperationException("This connection does not need an import retry.");
        session.Status = "Importing"; session.FailureCode = null; session.Revision++; await db.SaveChangesAsync(ct);
    }

    public async Task ImportAsync(Guid setupId, CancellationToken ct)
    {
        var s = await db.PluginAccountSetupSessions.SingleOrDefaultAsync(x => x.Id == setupId, ct);
        if (s is null || s.Status != "Importing" || s.LeaseUntil > DateTimeOffset.UtcNow) return;
        var owner = Guid.NewGuid(); s.LeaseOwner = owner; s.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(2); s.Revision++;
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return; }
        try
        {
            var (_, manifest, _) = await Current(s, ct);
            if (managed?.UsesManaged(manifest.Connections.Single(x => x.Id == manifest.Accounts!.Connection).ProviderProfile) == true)
            { await ImportManaged(s, ct); return; }
            var a = manifest.Accounts!;
            async Task<JsonElement> Invoke(string capability, JsonElement input)
            {
                var (_, current, connectionDeclaration) = await Current(s, ct);
                var op = current.ProviderOperations.Single(x => x.Capability == capability && x.Effect == "read" && x.Http is { Bootstrap: true, Method: "GET" });
                if (!List<string>((await db.AgentInstallationGrants.AsNoTracking().SingleAsync(x => x.AgentInstallationId == s.PluginInstallationId, ct)).ProvidedCapabilitiesJson).Contains(capability) ||
                    !current.Provides.Any(x => x.Name == capability && x.RiskClass == "bootstrap")) throw new UnauthorizedAccessException("The bootstrap read is not approved.");
                var authorization = await db.PluginConnections.AsNoTracking().SingleAsync(x => x.Id == s.ConnectionId && x.AgentInstallationId == s.PluginInstallationId, ct);
                if (authorization.Status != PluginConnectionStatus.Connected || op.Http!.Connection != connectionDeclaration.Id ||
                    op.Http.ScopeSets.SelectMany(id => connectionDeclaration.ScopeSets.Single(x => x.Id == id).Scopes)
                        .Except(List<string>(authorization.GrantedScopesJson), StringComparer.Ordinal).Any()) throw new UnauthorizedAccessException("The bootstrap permission is unavailable.");
                RequestSchemaValidator.Validate(input, op.InputSchema);
                var request = ConnectorRequestMaterializer.Prepare(op, input, "");
                async Task Revalidate(CancellationToken cancel)
                {
                    await Current(s, cancel);
                    if (!await db.PluginAccountSetupSessions.AsNoTracking().AnyAsync(x => x.Id == s.Id && x.Status == "Importing" &&
                        x.LeaseOwner == owner && x.LeaseUntil > DateTimeOffset.UtcNow, cancel) ||
                        !await db.PluginConnections.AsNoTracking().AnyAsync(x => x.Id == s.ConnectionId && x.Status == PluginConnectionStatus.Connected &&
                            x.AuthorizationRevision == authorization.AuthorizationRevision, cancel)) throw new UnauthorizedAccessException("The account import authority changed.");
                }
                var response = await transport.SendAsync(s.PluginInstallationId, s.ConnectionId, request, Revalidate, ct);
                if (response.StatusCode is 401 or 403 && s.IsMaintenance)
                {
                    var expired=await db.PluginConnections.SingleAsync(x=>x.Id==s.ConnectionId,ct);
                    expired.Status=PluginConnectionStatus.ReauthorizationRequired; expired.AuthorizationRevision++;
                    await db.SaveChangesAsync(ct);
                    throw new UnauthorizedAccessException("Account consent needs attention.");
                }
                if (response.StatusCode is < 200 or >= 300) throw new InvalidOperationException("The provider could not complete account discovery.");
                using var document = JsonDocument.Parse(response.Body, new JsonDocumentOptions { MaxDepth = 32 });
                _ = ConnectorRequestMaterializer.Canonical(document.RootElement); RequestSchemaValidator.Validate(document.RootElement, op.OutputSchema);
                await Revalidate(ct); return document.RootElement.Clone();
            }
            if (a.Identity is { } identity && s.VerifiedSubject is null)
            {
                var evidence = await Invoke(identity.Capability, JsonSerializer.SerializeToElement(new { }));
                s.VerifiedSubject = Field(evidence, identity.SubjectPointer, true);
                s.VerifiedName = identity.NamePointer is null ? null : Field(evidence, identity.NamePointer, false);
                s.IdentityAuthority = manifest.ProviderOperations.Single(x => x.Capability == identity.Capability).Http!.Endpoint;
                s.Revision++; await db.SaveChangesAsync(ct);
            }
            var observed = List<ConnectorAccountChoice>(s.ObservedResourcesJson).ToDictionary(x => x.Id, StringComparer.Ordinal);
            if (s.PagesImported > 0 && s.PageToken is null) { await FinishImport(s, observed.Values.ToArray(), ct); return; }
            var seen = List<string>(s.SeenPageTokensJson).ToHashSet(StringComparer.Ordinal);
            while (s.PagesImported < 1000)
            {
                var input = new Dictionary<string, string>();
                if (s.PageToken is not null) input.Add(a.PageTokenInput!, s.PageToken);
                var page = await Invoke(a.DiscoveryCapability, JsonSerializer.SerializeToElement(input));
                foreach (var row in ConnectorAccountProjection.Read(page, a.ResourceProjection with { NextPageTokenPointer = null }))
                {
                    if (observed.TryGetValue(row.Id, out var previous) && previous != row) throw new InvalidOperationException("Conflicting resource identities were returned.");
                    observed[row.Id] = row;
                    if (observed.Count > a.MaximumResources) throw new InvalidOperationException("The resource collection limit was exceeded.");
                }
                var next = a.ResourceProjection.NextPageTokenPointer is { } pointer ? Field(page, pointer, false, 2048) : null;
                if (next is not null && (!seen.Add(next) || a.PageTokenInput is null)) throw new InvalidOperationException("A repeated discovery continuation was rejected.");
                s.PagesImported++; s.PageToken = next; s.ObservedResourcesJson = JsonSerializer.Serialize(observed.Values, Json);
                s.SeenPageTokensJson = JsonSerializer.Serialize(seen); s.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(2); s.Revision++;
                await db.SaveChangesAsync(ct);
                if (next is null) { await FinishImport(s, observed.Values.ToArray(), ct); return; }
            }
            throw new InvalidOperationException("The discovery page budget was exceeded.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or JsonException or HttpRequestException)
        {
            db.ChangeTracker.Clear();
            s = await db.PluginAccountSetupSessions.SingleAsync(x => x.Id == setupId, ct);
            if (s.Status != "Importing" || s.LeaseOwner != owner) return;
            s.Status = "ImportFailed"; s.FailureCode = error is UnauthorizedAccessException ? "connection_changed" : "discovery_failed";
            if(s.IsMaintenance && error is UnauthorizedAccessException)
            {
                var authorization=await db.PluginConnections.SingleAsync(x=>x.Id==s.ConnectionId,ct);
                if(authorization.Status==PluginConnectionStatus.Connected) { authorization.Status=PluginConnectionStatus.ReauthorizationRequired; authorization.AuthorizationRevision++; }
            }
            s.LeaseOwner = null; s.LeaseUntil = null; s.Revision++;
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { }
        }
    }

    private async Task FinishImport(PluginAccountSetupSession s, IReadOnlyList<ConnectorAccountChoice> rows, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var (_, manifest, _) = await Current(s, ct);
        var connection = await db.PluginConnections.SingleAsync(x => x.Id == s.ConnectionId && x.AgentInstallationId == s.PluginInstallationId, ct);
        if (connection.Status != PluginConnectionStatus.Connected) throw new UnauthorizedAccessException("The authorization is no longer active.");
        var group = await db.PluginAccountGroups.SingleAsync(x => x.Id == s.AccountGroupId, ct);
        if(s.IsMaintenance && group.IdentityKind==PluginAccountIdentityKind.ProviderSubject &&
            (group.VerifiedSubject!=s.VerifiedSubject || group.IdentityAuthority!=s.IdentityAuthority))
            throw new UnauthorizedAccessException("Account identity changed during its periodic authorization check.");
        var previous = s.ReplacesConnectionId is { } id ? await db.PluginConnections.SingleAsync(x => x.Id == id, ct) : null;
        if (previous is not null)
        {
            var oldResourceIds = await db.PluginAccountResourceAuthorizations.AsNoTracking().Where(x => x.ConnectionId == previous.Id && x.IsAvailable)
                .Join(db.PluginAccountResources, p => p.ResourceId, r => r.Id, (p, r) => r.ExternalResourceId).ToArrayAsync(ct);
            if (previous.ExternalAccountId is { } oldSubject && oldSubject != s.VerifiedSubject || oldResourceIds.Except(rows.Select(x => x.Id), StringComparer.Ordinal).Any())
                throw new UnauthorizedAccessException("Reconnect must preserve the verified account and existing resources.");
        }
        if (s.VerifiedSubject is not null)
        {
            if (previous is not null && group.IdentityKind == PluginAccountIdentityKind.ProviderSubject &&
                (group.VerifiedSubject != s.VerifiedSubject || group.IdentityAuthority != s.IdentityAuthority)) throw new UnauthorizedAccessException("A different provider account was selected.");
            if (previous is null)
            {
                var existing = await db.PluginAccountGroups.SingleOrDefaultAsync(x => x.Id != group.Id && x.PluginInstallationId == s.PluginInstallationId &&
                    x.ProviderProfile == group.ProviderProfile && x.VerifiedSubject == s.VerifiedSubject && x.IdentityAuthority == s.IdentityAuthority && x.State == PluginAccountState.Connected, ct);
                if (existing is not null) { group.State = PluginAccountState.Disconnected; group.Revision++; group = existing; }
            }
            group.IdentityKind = PluginAccountIdentityKind.ProviderSubject; group.VerifiedSubject = s.VerifiedSubject; group.IdentityAuthority = s.IdentityAuthority;
            connection.ExternalAccountId = s.VerifiedSubject;
        }
        var now = DateTimeOffset.UtcNow;
        group.DisplayName = s.VerifiedName ?? rows.FirstOrDefault()?.Name ?? manifest.Accounts!.AccountLabel;
        group.State = PluginAccountState.Connected; group.UpdatedAt = now;
        if(!s.IsMaintenance) group.Revision++;
        connection.AccountGroupId = group.Id; connection.ExternalAccountName = group.DisplayName;
        var resourceIds = new List<Guid>();
        foreach (var row in rows)
        {
            var resource = await db.PluginAccountResources.SingleOrDefaultAsync(x => x.AccountGroupId == group.Id &&
                x.PluginInstallationId == s.PluginInstallationId && x.ExternalResourceId == row.Id && x.ResourceType == manifest.Accounts!.ResourceType, ct);
            if (resource is null)
            {
                resource = new() { Id = Guid.NewGuid(), AccountGroupId = group.Id, PluginInstallationId = s.PluginInstallationId,
                    ExternalResourceId = row.Id, ResourceType = manifest.Accounts!.ResourceType };
                db.PluginAccountResources.Add(resource);
            }
            if (resource.DisplayName != row.Name || resource.Handle != row.Handle || !resource.IsAvailable) resource.Revision++;
            resource.DisplayName = row.Name; resource.Handle = row.Handle; resource.VerifiedAt = now; resource.UpdatedAt = now; resource.IsAvailable = true;
            resourceIds.Add(resource.Id);
            var proof=await db.PluginAccountResourceAuthorizations.SingleOrDefaultAsync(x=>x.ConnectionId==connection.Id && x.ResourceId==resource.Id,ct);
            if(proof==null) { proof=new(){ConnectionId=connection.Id,ResourceId=resource.Id}; db.PluginAccountResourceAuthorizations.Add(proof); }
            if(!proof.IsAvailable) proof.Revision++;
            proof.IsAvailable=true; proof.VerifiedAt=now;
        }
        if(s.IsMaintenance)
        {
            var missing=await db.PluginAccountResourceAuthorizations.Where(x=>x.ConnectionId==connection.Id && x.IsAvailable && !resourceIds.Contains(x.ResourceId)).ToArrayAsync(ct);
            foreach(var proof in missing)
            {
                proof.IsAvailable=false; proof.Revision++;
                var resource=await db.PluginAccountResources.SingleAsync(x=>x.Id==proof.ResourceId,ct);
                if(!await db.PluginAccountResourceAuthorizations.AnyAsync(x=>x.ResourceId==resource.Id && x.ConnectionId!=connection.Id && x.IsAvailable &&
                    db.PluginConnections.Any(c=>c.Id==x.ConnectionId && c.Status==PluginConnectionStatus.Connected),ct))
                { resource.IsAvailable=false; resource.Revision++; }
                var affected=await db.AgentCapabilityBindings.Where(x=>x.PluginAccountConnectionId==connection.Id && x.PluginAccountResourceId==resource.Id && x.RevokedAt==null).ToArrayAsync(ct);
                foreach(var binding in affected) binding.RevokedAt=now;
                foreach(var business in affected.GroupBy(x=>x.OrganizationId))
                    if(Guid.TryParse(business.Key,out var org))
                    {
                        var consumers=business.Select(x=>x.RequesterInstallationId).Distinct().ToArray();
                        await ConnectorStandingPolicyService.RevokeForConsumersAsync(db,org,consumers,ct);
                        foreach(var policy in await db.PluginStandingPolicies.Where(x=>x.OrganizationId==org && consumers.Contains(x.AgentInstallationId) && x.Status==PluginStandingPolicyStatus.Approved).ToArrayAsync(ct))
                        {policy.Status=PluginStandingPolicyStatus.Revoked;policy.RevokedAt=now;}
                    }
            }
        }
        if (previous is not null)
        {
            var previousKey = await PluginOAuthCredentialKeys.ResolveAsync(db, s.PluginInstallationId, previous.Id, ct);
            var managedPrevious = await secrets.GetAsync(s.PluginInstallationId, ManagedContextKey(previous.Id), ct) is not null;
            s.RetiredCredentialKey = managedPrevious ? ManagedContextKey(previous.Id) : previousKey;
            if (!managedPrevious && s.VerifiedSubject is not null && previous.ExternalAccountId == s.VerifiedSubject && previous.CredentialProfileHash == s.ProfileHash)
            {
                // Long/resumed imports may already have refreshed into a different active vault generation.
                var currentKey = await PluginOAuthCredentialKeys.ResolveAsync(db, s.PluginInstallationId, connection.Id, ct);
                var currentToken = JsonSerializer.Deserialize<PluginAccountTokenEnvelope>((await secrets.GetAsync(s.PluginInstallationId, currentKey, ct))!, Json)!;
                var oldJson = await secrets.GetAsync(s.PluginInstallationId, previousKey, ct);
                if (currentToken.RefreshToken is null && oldJson is not null)
                {
                    var oldToken = JsonSerializer.Deserialize<PluginAccountTokenEnvelope>(oldJson, Json)!;
                    await secrets.SetAsync(s.PluginInstallationId, currentKey, JsonSerializer.Serialize(currentToken with { RefreshToken = oldToken.RefreshToken }, Json), ct);
                }
            }
            previous.Status = PluginConnectionStatus.Revoked; previous.AuthorizationRevision++; previous.UpdatedAt = now;
            var bindings = await db.AgentCapabilityBindings.Where(x => x.PluginAccountConnectionId == previous.Id && x.RevokedAt == null).ToArrayAsync(ct);
            foreach (var binding in bindings)
            {
                if (binding.PluginAccountResourceId is { } resourceId && resourceIds.Contains(resourceId)) binding.PluginAccountConnectionId = connection.Id;
                else binding.RevokedAt = now;
            }
            foreach (var business in bindings.GroupBy(x => x.OrganizationId))
                if (Guid.TryParse(business.Key, out var org)) await ConnectorStandingPolicyService.RevokeForConsumersAsync(db, org, business.Select(x => x.RequesterInstallationId).Distinct().ToArray(), ct);
            // Local retirement only: revoking a superseded provider token could revoke the new grant too.
        }
        s.AccountGroupId = group.Id; s.Status = rows.Count == 0 ? "NoResources" : "Ready"; s.LeaseOwner = null; s.LeaseUntil = null; s.Revision++;
        connection.NextInventoryCheckAt=now.AddHours(manifest.Accounts!.Lifecycle.AuthorizationCheckHours);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("plugin-account.discovery.completed", nameof(PluginAccountSetupSession), s.Id,
            "The complete provider-verified resource inventory was imported. No business access was granted.", cancellationToken: ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }
    private static string? Field(JsonElement value, string pointer, bool required, int maximum = 512)
    {
        var node = ConnectorRequestMaterializer.At(value, pointer);
        if (!required && (node is null || node.Value.ValueKind == JsonValueKind.Null || node.Value.ValueKind == JsonValueKind.String && node.Value.GetString() == "")) return null;
        if (node is not { ValueKind: JsonValueKind.String } || string.IsNullOrWhiteSpace(node.Value.GetString()) ||
            node.Value.GetString()!.Length > maximum || node.Value.GetString()!.Any(char.IsControl)) throw new InvalidOperationException("Invalid provider discovery field.");
        return node.Value.GetString();
    }
}

internal sealed record PluginAccountTokenEnvelope(string AccessToken, string? RefreshToken, string TokenType, DateTimeOffset ExpiresAt);
