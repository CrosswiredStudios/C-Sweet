using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.SourceControl;

namespace CSweet.TrustedServices;

public sealed partial class GitHubAppClient
{
    public async Task<DeliveryBranchResult> DeliveryBranchAsync(DeliveryBranchOperation operation, CancellationToken ct)
    {
        InternalGitRepositoryStore.ValidateBranch(operation.SourceBranch);
        InternalGitRepositoryStore.ValidateBranch(operation.TargetBranch);
        if (operation.InstallationId is not { } installation || string.IsNullOrWhiteSpace(operation.IdempotencyKey) ||
            operation.IdempotencyKey.Length > 200) throw new ArgumentException("A trusted installation and operation identity are required.");
        if (operation.Operation == "ensure")
            throw new InvalidOperationException("GitHub delivery activation requires managed-branch write restrictions and atomic comparison of accepted source and target. The configured provider does not supply these guarantees. Select Internal Git or configure a supported protected promotion provider; no branch or existing protection was changed.");
        var token = await CreateInstallationTokenAsync(installation, ct);
        var root = $"repos/{Escape(operation.Owner)}/{Escape(operation.Repository)}";
        async Task<JsonElement?> Send(HttpMethod method, string path, object? body = null)
        {
            using var request = CreateInstallationRequest(method, root + path, token);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"GitHub rejected delivery integration ({(int)response.StatusCode}); restore branch permissions, required checks, or resolve conflicts.");
            if (response.StatusCode == HttpStatusCode.NoContent) return JsonSerializer.SerializeToElement(new { });
            return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct)).Clone();
        }
        async Task<string?> Ref(string branch)
        {
            var value = await Send(HttpMethod.Get, "/git/ref/heads/" + Escape(branch));
            return value?.GetProperty("object").GetProperty("sha").GetString();
        }
        var target = await Ref(operation.TargetBranch) ?? throw new InvalidOperationException("The delivery target branch is missing.");
        var source = await Ref(operation.SourceBranch);
        if (operation.Operation == "ensure" && source is null)
        {
            await Send(HttpMethod.Post, "/git/refs", new { @ref = "refs/heads/" + operation.SourceBranch, sha = target });
            source = target;
        }
        if (source is null) throw new InvalidOperationException("The delivery source branch is missing.");
        if (operation.Operation is "ensure" or "inspect") return new(source, target, null);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation.IdempotencyKey))).ToLowerInvariant();
        var candidateBranch = "codex/candidate/" + key;
        if (operation.Operation == "promote" && target == operation.CandidateCommitSha)
            return new(operation.ExpectedSourceSha!, operation.ExpectedTargetSha!, target, true);
        if (source != operation.ExpectedSourceSha || target != operation.ExpectedTargetSha)
            return new(source, target, null, Error: "The source or target changed; prepare and validate a new candidate.");
        if (operation.Operation == "candidate")
        {
            var prior = await Ref(candidateBranch);
            if (prior is null)
            {
                await Send(HttpMethod.Post, "/git/refs", new { @ref = "refs/heads/" + candidateBranch, sha = target });
                await Send(HttpMethod.Post, "/merges", new { @base = candidateBranch, head = source,
                    commit_message = "C-Sweet delivery candidate " + key });
            }
            return new(source, target, await Ref(candidateBranch));
        }
        if (operation.Operation != "promote" || operation.CandidateCommitSha is not { } candidate)
            throw new ArgumentException("Unsupported delivery branch operation.");
        // A non-forced ref update checks ancestry, not the accepted target SHA.
        // Until a protected provider operation supplies atomic target comparison,
        // allowing this could promote against an unreviewed concurrent target.
        return new(source, target, null, Error: "GitHub cannot atomically compare this accepted source and target through the configured ref operation. Configure a supported protected promotion provider before retrying; no branch was changed.");
    }
}
