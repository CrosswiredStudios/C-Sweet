using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.SourceControl;

namespace CSweet.TrustedServices;

public sealed partial class GitHubAppClient
{
    public async Task<string> EnsureWorkspacePullRequestAsync(GitHubSnapshotOperation operation, string sha, string token, CancellationToken ct)
    {
        var workspace = operation.Workspace;
        ValidateRepositoryCoordinates(operation.Owner, operation.Repository); InternalGitRepositoryStore.ValidateSha(sha);
        var prefix = $"repos/{Escape(operation.Owner)}/{Escape(operation.Repository)}/pulls";
        using var list = CreateInstallationRequest(HttpMethod.Get, prefix + $"?state=all&head={Escape(operation.Owner + ":" + workspace.Branch)}&base={Escape(workspace.DefaultBranch)}&per_page=100", token);
        using var response = await http.SendAsync(list, ct); await EnsureSuccessAsync(response, ct);
        var existing = await response.Content.ReadFromJsonAsync<List<JsonElement>>(ct) ?? [];
        if (existing.Count > 0)
        {
            var open = existing.Where(p => p.GetProperty("state").GetString() == "open").ToList();
            if (open.Count != 1) throw new InvalidOperationException("The workspace pull request is closed or ambiguous. Use a new work branch.");
            return Verify(open[0]);
        }
        using var create = CreateInstallationRequest(HttpMethod.Post, prefix, token);
        create.Content = JsonContent.Create(new { title = operation.ProposedChangeTitle, body = operation.ProposedChangeBody ?? "",
            head = workspace.Branch, @base = workspace.DefaultBranch, maintainer_can_modify = false });
        using var created = await http.SendAsync(create, ct);
        await EnsureSuccessAsync(created, ct);
        return Verify(await created.Content.ReadFromJsonAsync<JsonElement>(ct));

        string Verify(JsonElement pull)
        {
            var head = pull.GetProperty("head"); var target = pull.GetProperty("base");
            if (pull.GetProperty("state").GetString() != "open" || head.GetProperty("sha").GetString() != sha ||
                head.GetProperty("ref").GetString() != workspace.Branch || target.GetProperty("ref").GetString() != workspace.DefaultBranch ||
                head.GetProperty("repo").GetProperty("id").GetInt64() != operation.ExternalRepositoryId || target.GetProperty("repo").GetProperty("id").GetInt64() != operation.ExternalRepositoryId)
                throw new InvalidOperationException("GitHub did not confirm the exact workspace pull request.");
            var number = pull.GetProperty("number").GetInt32(); if (number <= 0) throw new InvalidOperationException("GitHub returned an invalid pull request.");
            return $"https://github.com/{operation.Owner}/{operation.Repository}/pull/{number}";
        }
    }
}
