using System.Net.Http.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UI.Components.Projects;

public partial class ProjectReleases
{
    private readonly Dictionary<Guid, ResolutionInput> _resolutions = [];
    private readonly Dictionary<string, string> _operationKeys = [];
    private string OperationKey(string action, Guid id, long revision)
    {
        var identity = $"{action}:{id}:{revision}";
        if (!_operationKeys.TryGetValue(identity, out var key)) _operationKeys[identity] = key = Guid.NewGuid().ToString("N");
        return key;
    }
    private static Guid[] ParseIds(string value) => value.Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Guid.Parse).Distinct().ToArray();
    private ResolutionInput Resolution(Guid finding)
    { if (!_resolutions.TryGetValue(finding, out var input)) _resolutions[finding] = input = new(); return input; }
    private sealed class ResolutionInput { public string Tasks { get; set; } = ""; public string Evidence { get; set; } = ""; }
}
