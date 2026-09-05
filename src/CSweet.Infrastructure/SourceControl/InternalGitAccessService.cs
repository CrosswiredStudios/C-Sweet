using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

public sealed class InternalGitAccessService(CSweetDbContext db, ITrustedSourceControlHostClient host, IAuditEventWriter audit, TimeProvider clock)
{
    private sealed record Access(Guid RepositoryId, Guid UserId, string Name, string Hash, bool CanPush, bool AllowDefaultBranchWrites, DateTimeOffset ExpiresAt);
    private static Access Read(SourceControlCredential credential) => JsonSerializer.Deserialize<Access>(credential.ProtectedPayload)
        ?? throw new InvalidOperationException("Credential metadata is invalid.");

    public async Task<CreatedInternalGitAccess> CreateAsync(Guid business, Guid repositoryId, Guid user, CreateInternalGitAccessRequest request, CancellationToken ct)
    {
        var actor = await MemberAsync(business, user, ct);
        var repository = await RepositoryAsync(business, repositoryId, ct);
        if (request.CanPush && actor.PermissionLevel < OrganizationPermissionLevel.Manager) throw new UnauthorizedAccessException("Manager permission is required for push access.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100 || request.LifetimeDays is < 1 or > 90 || (request.AllowDefaultBranchWrites && !request.CanPush))
            throw new ArgumentException("Choose a credential name and a lifetime from 1 to 90 days. Default-branch access requires push permission.");
        var id = Guid.NewGuid();
        var token = $"csweet_git_{id:N}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
        var value = new Access(repositoryId, user, request.Name.Trim(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
            request.CanPush, request.AllowDefaultBranchWrites, clock.GetUtcNow().AddDays(request.LifetimeDays));
        var credential = new SourceControlCredential { Id = id, OrganizationId = business, ConnectionId = repository.ConnectionId, Kind = SourceControlCredentialKind.InternalGitAccess,
            ProtectedPayload = JsonSerializer.Serialize(value), ProtectionVersion = "sha256-v1", CreatedAt = clock.GetUtcNow() };
        db.SourceControlCredentials.Add(credential); await db.SaveChangesAsync(ct);
        await RecordAsync(business, repositoryId, user, "CredentialCreated", new { credential.Id, value.Name, value.CanPush, value.AllowDefaultBranchWrites, value.ExpiresAt }, ct);
        return new(Summary(credential, value), "csweet", token);
    }
    public async Task<IReadOnlyList<InternalGitAccessSummary>> ListAsync(Guid business, Guid repositoryId, Guid user, CancellationToken ct)
    {
        var actor = await MemberAsync(business, user, ct); var repository = await RepositoryAsync(business, repositoryId, ct);
        var credentials = await db.SourceControlCredentials.AsNoTracking().Where(c => c.OrganizationId == business && c.ConnectionId == repository.ConnectionId && c.Kind == SourceControlCredentialKind.InternalGitAccess).ToListAsync(ct);
        return credentials.Select(c => (Credential: c, Access: Read(c))).Where(c => c.Access.RepositoryId == repositoryId &&
            (c.Access.UserId == user || actor.PermissionLevel >= OrganizationPermissionLevel.Manager)).Select(c => Summary(c.Credential, c.Access)).ToList();
    }
    public async Task<bool> RevokeAsync(Guid business, Guid repositoryId, Guid user, Guid id, CancellationToken ct)
    {
        var actor = await MemberAsync(business, user, ct);
        var credential = await db.SourceControlCredentials.SingleOrDefaultAsync(c => c.Id == id && c.OrganizationId == business && c.Kind == SourceControlCredentialKind.InternalGitAccess, ct)
            ?? throw new KeyNotFoundException("Credential not found.");
        var access = Read(credential);
        if (access.RepositoryId != repositoryId || (access.UserId != user && actor.PermissionLevel < OrganizationPermissionLevel.Manager)) throw new UnauthorizedAccessException();
        credential.RevokedAt ??= clock.GetUtcNow(); await db.SaveChangesAsync(ct);
        await RecordAsync(business, repositoryId, user, "CredentialRevoked", new { credential.Id }, ct); return true;
    }
    public async Task AuthorizeAsync(Guid business, Guid repositoryId, string token, string service, CancellationToken ct) =>
        _ = await ValidateAsync(business, repositoryId, token, service, ct);

    private async Task<(SourceControlCredential Credential, Access Permission, SourceControlRepository Repository)> ValidateAsync(
        Guid business, Guid repositoryId, string token, string service, CancellationToken ct)
    {
        if (token.Length != 108 || !token.StartsWith("csweet_git_", StringComparison.Ordinal) || !Guid.TryParseExact(token.AsSpan(11, 32), "N", out var id))
            throw new UnauthorizedAccessException();
        var credential = await db.SourceControlCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.OrganizationId == business && c.Kind == SourceControlCredentialKind.InternalGitAccess && c.RevokedAt == null, ct)
            ?? throw new UnauthorizedAccessException();
        var access = Read(credential);
        if (access.RepositoryId != repositoryId || access.ExpiresAt <= clock.GetUtcNow() ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(access.Hash), SHA256.HashData(Encoding.UTF8.GetBytes(token)))) throw new UnauthorizedAccessException();
        var actor = await MemberAsync(business, access.UserId, ct); var repository = await RepositoryAsync(business, repositoryId, ct);
        if (credential.ConnectionId != repository.ConnectionId) throw new UnauthorizedAccessException();
        var push = service == "git-receive-pack";
        if (service is not ("git-receive-pack" or "git-upload-pack")) throw new ArgumentException("Unsupported Git service.");
        if (push && (!access.CanPush || actor.PermissionLevel < OrganizationPermissionLevel.Manager || repository.ArchivedAt is not null || repository.Status != SourceControlRepositoryStatus.Ready))
            throw new UnauthorizedAccessException("Push access is unavailable.");
        return (credential, access, repository);
    }

    public async Task<InternalGitHttpResponse> ExchangeAsync(Guid business, Guid repositoryId, string token, string service, bool advertise, byte[] body, CancellationToken ct)
    {
        var (credential, access, repository) = await ValidateAsync(business, repositoryId, token, service, ct);
        var push = service == "git-receive-pack";
        var protectedBranches = await db.SourceControlWorkspaces.AsNoTracking().Where(w => w.OrganizationId == business && w.RepositoryId == repositoryId &&
            w.Status != SourceControlWorkspaceStatus.Removed && w.Status != SourceControlWorkspaceStatus.Failed).Select(w => w.BranchName).Distinct().ToListAsync(ct);
        if (!access.AllowDefaultBranchWrites) protectedBranches.Add(repository.DefaultBranch);
        if (push && !advertise) await RecordAsync(business, repositoryId, access.UserId, "PushStarted", new { credential.Id }, ct);
        var result = await host.ExchangeInternalGitAsync(new(business, repositoryId, service, advertise, body, protectedBranches), ct);
        if (push && !advertise) await RecordAsync(business, repositoryId, access.UserId, "PushTransferCompleted", new { credential.Id }, ct);
        return result;
    }
    public async Task<InternalGitLfsTransferResult> TransferLfsAsync(Guid business, Guid repository, string token, string operation, string oid, long size, byte[] body, CancellationToken ct)
    {
        await ValidateAsync(business, repository, token, operation == "upload" ? "git-receive-pack" : "git-upload-pack", ct);
        if (operation == "upload" && (body.LongLength != size || !string.Equals(Convert.ToHexString(SHA256.HashData(body)), oid, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("LFS content must match its declared SHA-256 and size.");
        return await host.TransferInternalLfsAsync(new(business, repository, operation, oid, size, body), ct);
    }

    private async Task<OrganizationUser> MemberAsync(Guid business, Guid user, CancellationToken ct) =>
        await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(u => u.OrganizationId == business && u.ApplicationUserId == user && u.IsActive && u.EmployeeType == EmployeeType.Human, ct)
            ?? throw new UnauthorizedAccessException("Active human business membership is required.");
    private async Task<SourceControlRepository> RepositoryAsync(Guid business, Guid repositoryId, CancellationToken ct) =>
        await db.SourceControlRepositories.AsNoTracking().Include(r => r.Connection).SingleOrDefaultAsync(r => r.OrganizationId == business && r.Id == repositoryId &&
            r.IsPrivate && r.Connection!.Provider == SourceControlProvider.InternalGit && r.Connection.Status == SourceControlConnectionStatus.Connected &&
            (r.Status == SourceControlRepositoryStatus.Ready || r.Status == SourceControlRepositoryStatus.Archived), ct) ?? throw new UnauthorizedAccessException("Repository is unavailable.");
    private static InternalGitAccessSummary Summary(SourceControlCredential c, Access a) => new(c.Id, a.Name, a.CanPush, a.AllowDefaultBranchWrites, a.ExpiresAt, c.RevokedAt != null);
    private Task<Guid> RecordAsync(Guid business, Guid repository, Guid user, string operation, object metadata, CancellationToken ct) => audit.AppendAsync(new(
        "SourceControl.Git." + operation, Category: "SourceControl", OrganizationId: business, EntityType: "SourceControlRepository", EntityId: repository,
        Actor: new AuditActor("User", ApplicationUserId: user), MetadataJson: JsonSerializer.Serialize(metadata)), ct);
}
