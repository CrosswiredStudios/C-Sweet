using System.Globalization;
using System.Text.Json;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UI.Components.Employees;

public static class PersonalBoardPresentation
{
    public static readonly (string Label, string Status)[] Columns =
    [
        ("Backlog", Wire.PersonalTodoStatuses.Backlog), ("To Do", Wire.PersonalTodoStatuses.Ready),
        ("Doing", Wire.PersonalTodoStatuses.Running), ("Testing", "WaitingForApproval"), ("Blocked", Wire.PersonalTodoStatuses.Blocked),
        ("Done", Wire.PersonalTodoStatuses.Completed), ("Cancelled", Wire.PersonalTodoStatuses.Cancelled)
    ];

    public static string StatusLabel(string status) => Columns.FirstOrDefault(x => x.Status == status).Label ?? status;
    public static string Priority(string priority) => priority == "Normal" ? "Medium" : priority;
    public static string Initials(string name) => string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Take(2).Select(x => StringInfo.GetNextTextElement(x))).ToUpperInvariant();

    // Older agents stored a transport envelope in Description. Keep the original in the
    // details dialog, but show its human request when the known envelope contains one.
    public static string Description(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return string.Empty;
        try
        {
            using var json = JsonDocument.Parse(description);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String &&
                kind.GetString()?.StartsWith("csweet-", StringComparison.Ordinal) == true &&
                json.RootElement.TryGetProperty("request", out var request) && request.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(request.GetString()))
                return request.GetString()!;
        }
        catch (JsonException) { }
        return description;
    }

    public static string Excerpt(string? text, int length = 150)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var starts = StringInfo.ParseCombiningCharacters(text);
        return starts.Length <= length ? text : text[..starts[length]] + "…";
    }

    public static bool Matches(Wire.PersonalTodoItem item, IReadOnlySet<string> statuses,
        string priority, string archive) =>
        (statuses.Count == 0 || statuses.Contains(item.Status)) &&
        (priority.Length == 0 || Priority(item.Priority) == priority) &&
        (archive == "all" || (archive == "archived" ? item.ArchivedAt.HasValue : !item.ArchivedAt.HasValue));

    public static bool CanMove(Wire.PersonalTodoItem item, string status, bool canExecute, bool canManage) =>
        (item.PlanRootId is null || item.PlanRootId == item.Id) &&
        item.ArchivedAt is null && status != "WaitingForApproval" && item.Status != status && Columns.Any(x => x.Status == status) &&
        (canExecute || (canManage && status == Wire.PersonalTodoStatuses.Ready &&
            item.Status is Wire.PersonalTodoStatuses.Backlog or Wire.PersonalTodoStatuses.Blocked));
}
