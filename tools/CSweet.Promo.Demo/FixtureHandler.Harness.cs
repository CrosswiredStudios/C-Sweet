using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using CSweet.Contracts.Agents;

namespace CSweet.Promo.Demo;

public sealed partial class FixtureHandler
{
    private readonly Dictionary<Guid, HarnessPolicy> harnessPolicies = [];
    private readonly List<HarnessSkillRevision> harnessSkills = [];
    private async Task<HttpResponseMessage?> HarnessAsync(HttpRequestMessage request, string path, CancellationToken token)
    {
        var installation = data.Installations.FirstOrDefault(x => path.StartsWith($"/api/agents/{x.Id:D}/harness", StringComparison.Ordinal));
        if (installation is null) return null;
        var root = $"/api/agents/{installation.Id:D}/harness";
        if (path != root && path != root + "/skills" && !path.StartsWith(root + "/skills/", StringComparison.Ordinal)) return null;
        HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value, value.GetType()) };
        var policy = harnessPolicies.GetValueOrDefault(installation.Id) ?? new HarnessPolicy();
        if (request.Method == HttpMethod.Get && path == root)
            return Json(new HarnessWorkspace(policy, harnessSkills.ToArray(), [
                new("demo", "Info", "Design preview", "These controls use in-memory demonstration data. Saving here does not configure a live agent or spend money.", "/settings/agents"),
                new("provider", "Ready", "Model provider", "The demonstration provider is configured. No provider connection is made.", "/settings/llm-providers"),
                new("runtime", "Info", "Runtime", "No live workload is started by this preview.", "/settings/agents"),
                new("approvals", "Ready", "Pending operations", "No demonstration operations await approval.", $"/organizations/{data.OrganizationId:D}/approvals")
            ], DateTimeOffset.UtcNow, [new(data.Id(900), "Demo provider", "demo-model")]));
        if (request.Method == HttpMethod.Put && path == root)
        {
            var update = await request.Content!.ReadFromJsonAsync<SaveHarnessPolicy>(token);
            if (update is null || update.ExpectedRevision != policy.Revision) return Json(new { error = "Refresh the changed preview policy." }, HttpStatusCode.Conflict);
            harnessPolicies[installation.Id] = update.Policy with { Revision = policy.Revision + 1 };
            return Json(harnessPolicies[installation.Id]);
        }
        if (request.Method == HttpMethod.Post && path == root + "/skills")
        {
            var input = await request.Content!.ReadFromJsonAsync<PublishHarnessSkill>(token);
            if (input is null || string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Instructions)) return Json(new { error = "Enter a skill name and instructions." }, HttpStatusCode.BadRequest);
            var skill = new HarnessSkillRevision(Guid.NewGuid(), input.Name, harnessSkills.Count(x => x.Name == input.Name) + 1,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Instructions))).ToLowerInvariant(), input.Instructions, input.RequiredCapabilities, true, DateTimeOffset.UtcNow);
            harnessSkills.Add(skill); return Json(skill);
        }
        if (request.Method == HttpMethod.Delete && Guid.TryParse(path.Split('/')[^1], out var id))
        {
            var index = harnessSkills.FindIndex(x => x.Id == id);
            if (index < 0) return new(HttpStatusCode.NotFound);
            harnessSkills[index] = harnessSkills[index] with { Enabled = false }; return new(HttpStatusCode.NoContent);
        }
        return Json(new { error = "This preview operation is unavailable." }, HttpStatusCode.BadRequest);
    }
}
